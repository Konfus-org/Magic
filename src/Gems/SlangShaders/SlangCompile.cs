using System.Collections.Immutable;
using System.Text;
using Magic.Shaders;
using Magic.Utils;

namespace SlangShaders;

/// <summary>
/// A module's text with the name it imports as, in the order it is loaded: what one compile is made from.
/// </summary>
internal readonly record struct SlangModule(string Name, string Path, string Source);

/// <summary>
/// One compile of one entry: a session over the global one, the modules loaded into it in dependency order, the
/// entry found, specialised, linked and turned into SPIR-V, and everything released. Pure over its inputs, apart from
/// the compiler it drives, so a lock around it is all the thread safety it needs.
/// </summary>
internal static unsafe class SlangCompile
{
    /// <summary>
    /// What a compile is told beyond its sources, which the cache's version hashes with them.
    /// </summary>
    public static readonly ImmutableArray<string> Options =
    [
        "spirv_1_6",
        "row-major",
        "warnings-as-errors",
        "default-image-format-unknown",
        "entry-point-names",
        "emit-spirv-directly",
        "no-warning-41012",
    ];

    public static Result<CompiledShader> Compile(
        nint globalSession,
        ImmutableArray<SlangModule> modules,
        string entryModule,
        ShaderEntry entry,
        ImmutableArray<(string Module, string Type)> typeArguments
    )
    {
        using Session? session = Session.Create((void*)globalSession, out string error);
        if (session is null)
            return Result.Failure(error);
        void* module = null;
        List<nint> typeModules = [];
        foreach (SlangModule source in modules)
        {
            Result<nint> loaded = session.LoadModule(source);
            if (!loaded.Ok)
                return Result.Failure(loaded.Error);

            if (source.Name == entryModule)
                module = (void*)loaded.Value;
            else if (IsTypeModule(source.Name, typeArguments))
                typeModules.Add(loaded.Value);
        }

        if (module == null)
            return Result.Failure($"{entryModule} was not among the modules loaded.");

        Result<nint> entryPoint = session.FindEntryPoint(module, entry.Name);
        if (!entryPoint.Ok)
            return Result.Failure(entryPoint.Error);

        Result<nint> composed = session.Compose(module, (void*)entryPoint.Value, typeModules);
        if (!composed.Ok)
            return Result.Failure(composed.Error);

        void* program = (void*)composed.Value;
        if (typeArguments.Length > 0)
        {
            Result<nint> specialized = session.Specialize(program, typeArguments);
            if (!specialized.Ok)
                return Result.Failure(specialized.Error);

            program = (void*)specialized.Value;
        }

        Result<nint> linked = session.Link(program);
        if (!linked.Ok)
            return Result.Failure(linked.Error);

        Result<byte[]> code = Session.GetEntryPointCode((void*)linked.Value);
        if (!code.Ok)
            return Result.Failure(code.Error);

        (uint x, uint y, uint z) = session.GetGroupSize((void*)linked.Value, entry.Stage);

        return Result.Success(
            new CompiledShader
            {
                Spirv = code.Value,
                EntryPoint = entry.Name,
                Stage = entry.Stage,
                GroupSizeX = x,
                GroupSizeY = y,
                GroupSizeZ = z,
            }
        );
    }

    private static bool IsTypeModule(string module, ImmutableArray<(string Module, string Type)> typeArguments)
    {
        foreach ((string typeModule, _) in typeArguments)
        {
            if (typeModule == module)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A session and everything made in it, released together when it is disposed.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly List<nint> _owned = [];
        private readonly void* _session;

        private Session(void* session) => _session = session;

        public static Session? Create(void* globalSession, out string error)
        {
            error = string.Empty;
            fixed (byte* profileName = "spirv_1_6\0"u8)
            fixed (byte* all = "all\0"u8)
            fixed (byte* profileUpgrade = "41012\0"u8)
            {
                int profile = Slang.FindProfile(globalSession, profileName);
                if (profile == 0)
                {
                    error = "the compiler has no spirv_1_6 profile.";

                    return null;
                }

                Slang.CompilerOptionEntry* options = stackalloc Slang.CompilerOptionEntry[7];
                options[0] = Int(Slang.Option.EmitSpirvDirectly, 1);
                options[1] = Int(Slang.Option.VulkanUseEntryPointName, 1);
                options[2] = Int(Slang.Option.MatrixLayoutRow, 1);
                options[3] = Int(Slang.Option.DefaultImageFormatUnknown, 1);
                options[4] = Int(Slang.Option.Optimization, 1);
                options[5] = new Slang.CompilerOptionEntry
                {
                    Name = Slang.Option.WarningsAsErrors,
                    Value = new Slang.CompilerOptionValue { Kind = Slang.Target.OptionKindString, StringValue0 = all },
                };
                // The heap's image queries need capabilities past the bare profile, which the compiler adds per entry:
                // a warning on every entry that every GPU Magic runs on has no need of.
                options[6] = new Slang.CompilerOptionEntry
                {
                    Name = Slang.Option.DisableWarning,
                    Value = new Slang.CompilerOptionValue
                    {
                        Kind = Slang.Target.OptionKindString,
                        StringValue0 = profileUpgrade,
                    },
                };
                Slang.TargetDesc target = new()
                {
                    StructureSize = (nuint)sizeof(Slang.TargetDesc),
                    Format = Slang.Target.Spirv,
                    Profile = profile,
                };
                Slang.SessionDesc desc = new()
                {
                    StructureSize = (nuint)sizeof(Slang.SessionDesc),
                    Targets = &target,
                    TargetCount = 1,
                    DefaultMatrixLayoutMode = Slang.Target.MatrixLayoutRowMajor,
                    CompilerOptionEntries = options,
                    CompilerOptionEntryCount = 7,
                };
                void* session;
                int result = Slang.CreateSession(globalSession, &desc, &session);

                if (result < 0 || session == null)
                {
                    error = $"the compiler gave no session: {result:X}.";

                    return null;
                }

                return new Session(session);
            }
        }

        public static Result<byte[]> GetEntryPointCode(void* linked)
        {
            void* code;
            void* diagnostics = null;
            int result = Slang.GetEntryPointCode(linked, 0, 0, &code, &diagnostics);
            string messages = TakeDiagnostics(diagnostics);
            if (result < 0 || code == null)
                return Result.Failure($"the entry does not compile to SPIR-V:\n{messages}");

            byte[] bytes = new ReadOnlySpan<byte>(Slang.GetBlobPointer(code), (int)Slang.GetBlobSize(code)).ToArray();
            Slang.Release(code);

            return Result.Success(bytes);
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                Slang.Release((void*)_owned[i]);
            }

            Slang.Release(_session);
        }

        public Result<nint> LoadModule(in SlangModule module)
        {
            using Utf8 name = new(module.Name);
            using Utf8 path = new(module.Path);
            using Utf8 source = new(module.Source);
            void* diagnostics = null;
            void* loaded = Slang.LoadModuleFromSourceString(
                _session,
                name.Pointer,
                path.Pointer,
                source.Pointer,
                &diagnostics
            );
            string messages = TakeDiagnostics(diagnostics);
            if (loaded == null)
                return Result.Failure($"{module.Path} does not compile:\n{messages}");

            // The session owns its modules and frees them with itself.
            return Result.Success((nint)loaded);
        }

        public Result<nint> FindEntryPoint(void* module, string name)
        {
            using Utf8 entryName = new(name);
            void* entryPoint;
            int result = Slang.FindEntryPointByName(module, entryName.Pointer, &entryPoint);
            if (result < 0 || entryPoint == null)
                return Result.Failure($"the module has no entry point {name}; one has [shader(...)] on it.");

            _owned.Add((nint)entryPoint);

            return Result.Success((nint)entryPoint);
        }

        /// <summary>
        /// The entry's module, the entry point and the modules its type arguments are declared in as one program, so
        /// a type argument is found in the program's own layout.
        /// </summary>
        public Result<nint> Compose(void* module, void* entryPoint, List<nint> typeModules)
        {
            void** parts = stackalloc void*[2 + typeModules.Count];
            parts[0] = module;
            parts[1] = entryPoint;
            for (int i = 0; i < typeModules.Count; i++)
            {
                parts[2 + i] = (void*)typeModules[i];
            }

            void* composite;
            void* diagnostics = null;
            int result = Slang.CreateCompositeComponentType(
                _session,
                parts,
                2 + typeModules.Count,
                &composite,
                &diagnostics
            );
            string messages = TakeDiagnostics(diagnostics);
            if (result < 0 || composite == null)
                return Result.Failure($"the entry and its module do not compose:\n{messages}");

            _owned.Add((nint)composite);

            return Result.Success((nint)composite);
        }

        /// <summary>
        /// The program with its generic parameters bound to the named types, found through the program's own
        /// reflection, so a type comes from any module loaded.
        /// </summary>
        public Result<nint> Specialize(void* program, ImmutableArray<(string Module, string Type)> typeArguments)
        {
            void* diagnostics = null;
            void* layout = Slang.GetLayout(program, 0, &diagnostics);
            string messages = TakeDiagnostics(diagnostics);
            if (layout == null)
                return Result.Failure($"the program has no layout to specialise from:\n{messages}");

            Slang.SpecializationArg* args = stackalloc Slang.SpecializationArg[typeArguments.Length];
            for (int i = 0; i < typeArguments.Length; i++)
            {
                using Utf8 typeName = new(typeArguments[i].Type);
                void* type = Slang.spReflection_FindTypeByName(layout, typeName.Pointer);
                if (type == null)
                    return Result.Failure($"{typeArguments[i].Module} declares no type {typeArguments[i].Type}.");

                args[i] = new Slang.SpecializationArg { Kind = Slang.Target.SpecializationKindType, Type = type };
            }

            void* specialized;
            int result = Slang.Specialize(program, args, typeArguments.Length, &specialized, &diagnostics);
            messages = TakeDiagnostics(diagnostics);
            if (result < 0 || specialized == null)
                return Result.Failure($"the entry does not specialise to those types:\n{messages}");

            _owned.Add((nint)specialized);

            return Result.Success((nint)specialized);
        }

        public Result<nint> Link(void* program)
        {
            void* linked;
            void* diagnostics = null;
            int result = Slang.Link(program, &linked, &diagnostics);
            string messages = TakeDiagnostics(diagnostics);
            if (result < 0 || linked == null)
                return Result.Failure($"the program does not link:\n{messages}");

            _owned.Add((nint)linked);

            return Result.Success((nint)linked);
        }

        /// <summary>
        /// A compute entry's threads per group; zeros for another stage, or when the compiler cannot say.
        /// </summary>
        public (uint X, uint Y, uint Z) GetGroupSize(void* linked, ShaderStage stage)
        {
            if (stage != ShaderStage.Compute)
                return (0, 0, 0);

            void* diagnostics = null;
            void* layout = Slang.GetLayout(linked, 0, &diagnostics);
            TakeDiagnostics(diagnostics);
            if (layout == null)
                return (0, 0, 0);

            void* entryPoint = Slang.spReflection_getEntryPointByIndex(layout, 0);
            if (entryPoint == null)
                return (0, 0, 0);

            ulong* sizes = stackalloc ulong[3];
            Slang.spReflectionEntryPoint_getComputeThreadGroupSize(entryPoint, 3, sizes);

            return ((uint)sizes[0], (uint)sizes[1], (uint)sizes[2]);
        }

        private static Slang.CompilerOptionEntry Int(int name, int value) =>
            new()
            {
                Name = name,
                Value = new Slang.CompilerOptionValue { Kind = Slang.Target.OptionKindInt, IntValue0 = value },
            };

        /// <summary>
        /// The diagnostics' text, the blob released; empty when there were none.
        /// </summary>
        private static string TakeDiagnostics(void* diagnostics)
        {
            if (diagnostics == null)
                return string.Empty;

            string text = Encoding.UTF8.GetString(
                (byte*)Slang.GetBlobPointer(diagnostics),
                (int)Slang.GetBlobSize(diagnostics)
            );
            Slang.Release(diagnostics);

            return text.Trim();
        }
    }

    /// <summary>
    /// A string as the compiler reads it: UTF-8 bytes ending in 0, pinned while in use.
    /// </summary>
    private sealed class Utf8 : IDisposable
    {
        private readonly byte[] _bytes;
        private readonly System.Runtime.InteropServices.GCHandle _handle;

        public Utf8(string text)
        {
            _bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
            Encoding.UTF8.GetBytes(text, _bytes);
            _handle = System.Runtime.InteropServices.GCHandle.Alloc(
                _bytes,
                System.Runtime.InteropServices.GCHandleType.Pinned
            );
        }

        public byte* Pointer => (byte*)_handle.AddrOfPinnedObject();

        public void Dispose() => _handle.Free();
    }
}
