using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Assets;
using Magic.Files;
using Magic.Projects;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// A module's text with the name it imports as, in the order it is loaded: what one compile is made from.
/// </summary>
internal readonly record struct SlangModule(string Name, string Path, string Source);

/// <summary>
/// Compiles Slang entries to SPIR-V: the entry's module and every module it imports, found as <see cref="Shader"/>
/// assets, go into a session in dependency order, and the entry comes out compiled and cached under a hash of the
/// sources, so an unchanged entry never compiles twice. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed partial class SlangShaderCompilerGem : IGem, IShaderCompiler, ICache<CompiledShader>, IDisposable
{
    private const string ShadersFolder = "Shaders";
    private const string Extension = ".slang";

    private readonly AssetManager _assets;
    private readonly Caches _caches;

    /// <summary>
    /// One compile at a time: the compiler's global session is not for several threads at once.
    /// </summary>
    private readonly SemaphoreSlim _compiling = new(1, 1);

    /// <summary>
    /// The run's <see cref="App.Lifetime"/>, which ends before the gem is disposed, so compiles still waiting for their
    /// turn give up.
    /// </summary>
    private readonly CancellationToken _lifetime;

    private readonly unsafe void* _globalSession;

    public unsafe SlangShaderCompilerGem(App app, AssetManager assets, Caches caches)
    {
        _assets = assets;
        _caches = caches;
        _lifetime = app.Lifetime;
        _globalSession = Slang.CreateGlobalSession();
        if (_globalSession == null)
            throw new InvalidOperationException("The Slang compiler could not start.");
    }

    /// <summary>
    /// Raised when the bytes written change shape, and when the compiler's options or tag change.
    /// </summary>
    public int Version => 4;

    public unsafe void Dispose()
    {
        // The compile already running finishes before the session it uses is freed. Waiting for it is safe: the frame loop
        // has ended, and a compile needs no engine thread.
        _compiling.Wait();
        Slang.Release(_globalSession);
        Slang.slang_shutdown();
        _compiling.Dispose();
    }

    public async Task<Result<CompiledShader>> CompileAsync(ShaderEntry entry, CancellationToken cancel)
    {
        if (_assets.GetPath(entry.Module) is not { } entryPath)
            return Result.Failure($"the shader {entry.Module.Id} is not an asset.");

        List<(string Path, string Type)> typeArguments = [];
        foreach (ShaderTypeArgument argument in entry.TypeArguments)
        {
            if (_assets.GetPath(argument.Module) is not { } path)
                return Result.Failure($"the shader {argument.Module.Id} is not an asset.");

            typeArguments.Add((path, argument.Type));
        }

        Result<ImmutableArray<SlangModule>> modules = await LoadModulesAsync(
                [entryPath, .. typeArguments.Select(argument => argument.Path)],
                cancel
            )
            .ConfigureAwait(false);
        if (!modules.Ok)
            return Result.Failure(modules.Error);

        ulong key = GetKey(entryPath, entry, typeArguments);
        ulong version = GetVersion(modules.Value);
        Result<CompiledShader> cached = await _caches.ReadAsync(key, version, this, cancel).ConfigureAwait(false);
        if (cached.Ok)
            return cached;

        Result<CompiledShader> compiled = await RunAsync(
                () => Compile(modules.Value, entryPath, entry, typeArguments),
                cancel
            )
            .ConfigureAwait(false);
        if (!compiled.Ok)
            return Result.Failure($"{entryPath}: {entry.Name} did not compile: {compiled.Error}");

        await _caches.WriteAsync(key, version, compiled.Value, this, cancel).ConfigureAwait(false);

        return compiled;
    }

    /// <summary>
    /// The fields of the struct <paramref name="type"/> names, as a buffer lays them out; or why not. Not cached: a
    /// renderer asks once for each material shader it draws with.
    /// </summary>
    public async Task<Result<ImmutableArray<ShaderField>>> ReflectAsync(
        ShaderTypeArgument type,
        CancellationToken cancel
    )
    {
        if (_assets.GetPath(type.Module) is not { } path)
            return Result.Failure($"the shader {type.Module.Id} is not an asset.");

        Result<ImmutableArray<SlangModule>> modules = await LoadModulesAsync([path], cancel).ConfigureAwait(false);
        if (!modules.Ok)
            return Result.Failure(modules.Error);

        return await RunAsync(() => Reflect(modules.Value, path, type.Type), cancel).ConfigureAwait(false);
    }

    public void Write(CompiledShader value, IBufferWriter<byte> bytes)
    {
        byte[] name = Encoding.UTF8.GetBytes(value.EntryPoint);
        Span<byte> header = bytes.GetSpan(16);
        BinaryPrimitives.WriteUInt32LittleEndian(header, value.GroupSizeX);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], value.GroupSizeY);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], value.GroupSizeZ);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], name.Length);
        bytes.Advance(16);
        bytes.Write(name);
        bytes.Write(value.Spirv.Span);
    }

    public Result<CompiledShader> Read(ReadOnlySpan<byte> bytes)
    {
        // Bytes cut short throw from a slice, which the caches take as a dirty entry.
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);

        return Result.Success(
            new CompiledShader
            {
                GroupSizeX = BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                GroupSizeY = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
                GroupSizeZ = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
                EntryPoint = Encoding.UTF8.GetString(bytes.Slice(16, nameLength)),
                Spirv = bytes[(16 + nameLength)..].ToArray(),
            }
        );
    }

    /// <summary>
    /// <c>Shaders/Core/Heap.slang</c> as the compiler names it, <c>Core/Heap</c>, which is what <c>import Core.Heap;</c>
    /// asks for.
    /// </summary>
    private static string ToModuleName(string path)
    {
        string relative = path.StartsWith(ShadersFolder + "/", StringComparison.Ordinal)
            ? path[(ShadersFolder.Length + 1)..]
            : path;
        if (relative.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            relative = relative[..^Extension.Length];

        return relative;
    }

    [GeneratedRegex(@"^\s*(?:__exported\s+)?import\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    private static ulong GetKey(string entryPath, in ShaderEntry entry, List<(string Path, string Type)> typeArguments)
    {
        XxHash64 hash = new();
        hash.Append(Encoding.UTF8.GetBytes(entryPath));
        hash.Append(Encoding.UTF8.GetBytes(entry.Name));
        hash.Append(BitConverter.GetBytes((int)entry.Stage));
        foreach ((string path, string type) in typeArguments)
        {
            hash.Append(Encoding.UTF8.GetBytes(path));
            hash.Append(Encoding.UTF8.GetBytes(type));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// What the compile is made from: every module's text, in order.
    /// </summary>
    private static ulong GetVersion(ImmutableArray<SlangModule> modules)
    {
        XxHash64 hash = new();
        foreach (SlangModule module in modules)
        {
            hash.Append(Encoding.UTF8.GetBytes(module.Name));
            hash.Append(Encoding.UTF8.GetBytes(module.Source));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    private static Slang.CompilerOptionEntry Int(int name, int value) =>
        new()
        {
            Name = name,
            Value = new Slang.CompilerOptionValue { Kind = Slang.Target.OptionKindInt, IntValue0 = value },
        };

    /// <summary>
    /// The diagnostics' text, the blob released and the pointer cleared for the next call; empty when there were none.
    /// </summary>
    private static unsafe string TakeDiagnostics(ref void* diagnostics)
    {
        if (diagnostics == null)
            return string.Empty;

        string text = Encoding.UTF8.GetString(
            (byte*)Slang.GetBlobPointer(diagnostics),
            (int)Slang.GetBlobSize(diagnostics)
        );
        Slang.Release(diagnostics);
        diagnostics = null;

        return text.Trim();
    }

    /// <summary>
    /// One compile of one entry: a session over the global one, the modules loaded into it in dependency order, the
    /// entry found, specialised, linked and turned into SPIR-V, and everything released. Pure over its inputs, apart from
    /// the compiler it drives, so <see cref="_compiling"/> is all the thread safety it needs.
    /// </summary>
    private unsafe Result<CompiledShader> Compile(
        ImmutableArray<SlangModule> modules,
        string entryPath,
        in ShaderEntry entry,
        List<(string Path, string Type)> typeArguments
    )
    {
        List<nint> owned = [];
        void* diagnostics = null;
        string messages;
        int result;
        try
        {
            Result<nint> made = CreateSession();
            if (!made.Ok)
                return Result.Failure(made.Error);

            void* session = (void*)made.Value;
            owned.Add(made.Value);
            Result<Dictionary<string, nint>> loaded = LoadModules(session, modules);
            if (!loaded.Ok)
                return Result.Failure(loaded.Error);

            // The entry's module, and its type arguments' in the order they loaded.
            void* module = (void*)loaded.Value[entryPath];
            List<nint> typeModules =
            [
                .. modules
                    .Where(source =>
                        source.Path != entryPath && typeArguments.Any(argument => argument.Path == source.Path)
                    )
                    .Select(source => loaded.Value[source.Path]),
            ];

            // The entry point, composed with its module and the type arguments' modules into one program, so a type
            // argument is found in the program's own layout.
            void* entryPoint;
            fixed (byte* entryName = CString.ToBytes(entry.Name))
            {
                result = Slang.FindEntryPointByName(module, entryName, &entryPoint);
            }

            if (result < 0 || entryPoint == null)
                return Result.Failure($"the module has no entry point {entry.Name}; one has [shader(...)] on it.");

            owned.Add((nint)entryPoint);
            void** parts = stackalloc void*[2 + typeModules.Count];
            parts[0] = module;
            parts[1] = entryPoint;
            for (int i = 0; i < typeModules.Count; i++)
            {
                parts[2 + i] = (void*)typeModules[i];
            }

            void* program;
            result = Slang.CreateCompositeComponentType(session, parts, 2 + typeModules.Count, &program, &diagnostics);
            messages = TakeDiagnostics(ref diagnostics);
            if (result < 0 || program == null)
                return Result.Failure($"the entry and its module do not compose:\n{messages}");

            owned.Add((nint)program);

            // The program's generic parameters bound to the named types, found through its own reflection, so a type
            // comes from any module loaded.
            if (typeArguments.Count > 0)
            {
                void* layout = Slang.GetLayout(program, 0, &diagnostics);
                messages = TakeDiagnostics(ref diagnostics);
                if (layout == null)
                    return Result.Failure($"the program has no layout to specialise from:\n{messages}");

                Slang.SpecializationArg* args = stackalloc Slang.SpecializationArg[typeArguments.Count];
                for (int i = 0; i < typeArguments.Count; i++)
                {
                    void* type;
                    fixed (byte* typeName = CString.ToBytes(typeArguments[i].Type))
                    {
                        type = Slang.spReflection_FindTypeByName(layout, typeName);
                    }

                    if (type == null)
                        return Result.Failure($"{typeArguments[i].Path} declares no type {typeArguments[i].Type}.");

                    args[i] = new Slang.SpecializationArg { Kind = Slang.Target.SpecializationKindType, Type = type };
                }

                void* specialized;
                result = Slang.Specialize(program, args, typeArguments.Count, &specialized, &diagnostics);
                messages = TakeDiagnostics(ref diagnostics);
                if (result < 0 || specialized == null)
                    return Result.Failure($"the entry does not specialise to those types:\n{messages}");

                owned.Add((nint)specialized);
                program = specialized;
            }

            // Linked, then turned into SPIR-V.
            void* linked;
            result = Slang.Link(program, &linked, &diagnostics);
            messages = TakeDiagnostics(ref diagnostics);
            if (result < 0 || linked == null)
                return Result.Failure($"the program does not link:\n{messages}");

            owned.Add((nint)linked);
            void* code;
            result = Slang.GetEntryPointCode(linked, 0, 0, &code, &diagnostics);
            messages = TakeDiagnostics(ref diagnostics);
            if (result < 0 || code == null)
                return Result.Failure($"the entry does not compile to SPIR-V:\n{messages}");

            owned.Add((nint)code);
            byte[] spirv = new ReadOnlySpan<byte>(Slang.GetBlobPointer(code), (int)Slang.GetBlobSize(code)).ToArray();

            // A compute entry's threads per group; zeros for another stage, or when the compiler cannot say.
            ulong* sizes = stackalloc ulong[3] { 0, 0, 0 };
            if (entry.Stage == ShaderStage.Compute)
            {
                void* layout = Slang.GetLayout(linked, 0, &diagnostics);
                TakeDiagnostics(ref diagnostics);
                void* layoutEntry = layout == null ? null : Slang.spReflection_getEntryPointByIndex(layout, 0);
                if (layoutEntry != null)
                    Slang.spReflectionEntryPoint_getComputeThreadGroupSize(layoutEntry, 3, sizes);
            }

            return Result.Success(
                new CompiledShader
                {
                    Spirv = spirv,
                    EntryPoint = entry.Name,
                    GroupSizeX = (uint)sizes[0],
                    GroupSizeY = (uint)sizes[1],
                    GroupSizeZ = (uint)sizes[2],
                }
            );
        }
        finally
        {
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                Slang.Release((void*)owned[i]);
            }
        }
    }

    /// <summary>
    /// <paramref name="work"/> on a worker, once the compiler is free: one at a time, as the global session allows. Work
    /// that comes once the gem is disposed ends cancelled, as work still waiting for its turn does.
    /// </summary>
    private Task<T> RunAsync<T>(Func<T> work, CancellationToken cancel) =>
        Task.Run(
            async () =>
            {
                try
                {
                    using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(
                        cancel,
                        _lifetime
                    );
                    await _compiling.WaitAsync(waiting.Token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The shader compiler is disposed.");
                }

                try
                {
                    return work();
                }
                finally
                {
                    _compiling.Release();
                }
            },
            cancel
        );

    /// <summary>
    /// The session: SPIR-V 1.6, row-major, entry names kept, warnings as errors, and buffers laid out scalar, as C# packs
    /// a struct and the heap reads one. A change here raises Version.
    /// </summary>
    private unsafe Result<nint> CreateSession()
    {
        int result;
        void* session;
        fixed (byte* profileName = "spirv_1_6\0"u8)
        fixed (byte* all = "all\0"u8)
        fixed (byte* profileUpgrade = "41012\0"u8)
        {
            int profile = Slang.FindProfile(_globalSession, profileName);
            if (profile == 0)
                return Result.Failure("the compiler has no spirv_1_6 profile.");

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
                ForceGlslScalarBufferLayout = 1,
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
            result = Slang.CreateSession(_globalSession, &desc, &session);
            if (result < 0 || session == null)
                return Result.Failure($"the compiler gave no session: {result:X}.");
        }

        return Result.Success((nint)session);
    }

    /// <summary>
    /// <paramref name="modules"/> loaded into <paramref name="session"/>, which owns and frees them with itself, by their
    /// paths; or the first that does not compile.
    /// </summary>
    private unsafe Result<Dictionary<string, nint>> LoadModules(void* session, ImmutableArray<SlangModule> modules)
    {
        void* diagnostics = null;
        Dictionary<string, nint> loaded = new(StringComparer.Ordinal);
        foreach (SlangModule source in modules)
        {
            void* module;
            fixed (
                byte* name = CString.ToBytes(source.Name),
                    path = CString.ToBytes(source.Path),
                    text = CString.ToBytes(source.Source)
            )
            {
                module = Slang.LoadModuleFromSourceString(session, name, path, text, &diagnostics);
            }

            string messages = TakeDiagnostics(ref diagnostics);
            if (module == null)
                return Result.Failure($"{source.Path} does not compile:\n{messages}");

            loaded[source.Path] = (nint)module;
        }

        return Result.Success(loaded);
    }

    /// <summary>
    /// The fields of <paramref name="typeName"/> in the module at <paramref name="typePath"/> as the session lays them
    /// out: each one's name, offset and size, and a struct field's type's name.
    /// </summary>
    private unsafe Result<ImmutableArray<ShaderField>> Reflect(
        ImmutableArray<SlangModule> modules,
        string typePath,
        string typeName
    )
    {
        // Slang's SlangTypeKind of a struct, and its SlangParameterCategory of a field's bytes.
        const int Struct = 1;
        const int Uniform = 8;
        Result<nint> made = CreateSession();
        if (!made.Ok)
            return Result.Failure(made.Error);

        void* session = (void*)made.Value;
        try
        {
            Result<Dictionary<string, nint>> loaded = LoadModules(session, modules);
            if (!loaded.Ok)
                return Result.Failure(loaded.Error);

            void* diagnostics = null;
            void* layout = Slang.GetLayout((void*)loaded.Value[typePath], 0, &diagnostics);
            TakeDiagnostics(ref diagnostics);
            void* type;
            fixed (byte* named = CString.ToBytes(typeName))
            {
                type = layout == null ? null : Slang.spReflection_FindTypeByName(layout, named);
            }

            if (type == null)
                return Result.Failure($"{typePath} declares no type {typeName}.");

            void* typeLayout = Slang.spReflection_GetTypeLayout(layout, type, 0);
            uint count = Slang.spReflectionTypeLayout_GetFieldCount(typeLayout);
            ImmutableArray<ShaderField>.Builder fields = ImmutableArray.CreateBuilder<ShaderField>((int)count);
            for (uint i = 0; i < count; i++)
            {
                void* field = Slang.spReflectionTypeLayout_GetFieldByIndex(typeLayout, i);
                void* fieldLayout = Slang.spReflectionVariableLayout_GetTypeLayout(field);
                void* fieldType = Slang.spReflectionTypeLayout_GetType(fieldLayout);
                void* variable = Slang.spReflectionVariableLayout_GetVariable(field);
                fields.Add(
                    new ShaderField(
                        CString.Read(Slang.spReflectionVariable_GetName(variable)),
                        (uint)Slang.spReflectionVariableLayout_GetOffset(field, Uniform),
                        (uint)Slang.spReflectionTypeLayout_GetSize(fieldLayout, Uniform),
                        Slang.spReflectionType_GetKind(fieldType) == Struct
                            ? CString.Read(Slang.spReflectionType_GetName(fieldType))
                            : string.Empty
                    )
                );
            }

            return Result.Success(fields.MoveToImmutable());
        }
        finally
        {
            Slang.Release(session);
        }
    }

    /// <summary>
    /// The modules at <paramref name="roots"/> and everything they import, each once, imports before what imports
    /// them.
    /// </summary>
    private async Task<Result<ImmutableArray<SlangModule>>> LoadModulesAsync(
        List<string> roots,
        CancellationToken cancel
    )
    {
        List<SlangModule> ordered = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            Result visitedRoot = await VisitAsync(root).ConfigureAwait(false);
            if (!visitedRoot.Ok)
                return Result.Failure(visitedRoot.Error);
        }

        return Result.Success(ordered.ToImmutableArray());

        async Task<Result> VisitAsync(string path)
        {
            if (!visited.Add(path))
                return Result.Success();

            Handle<Shader> handle = _assets.Find<Shader>(path);
            if (handle == Handle<Shader>.None)
                return Result.Failure($"there is no {path}.");

            Result<Shader> loaded = await _assets.LoadAsync(handle, null, cancel).ConfigureAwait(false);
            if (!loaded.Ok)
                return Result.Failure($"{path} could not be read: {loaded.Error}");

            foreach (Match import in ImportLine().Matches(loaded.Value.Text))
            {
                string importedName = import.Groups[1].Value;
                Result visitedImport = await VisitAsync($"{ShadersFolder}/{importedName.Replace('.', '/')}{Extension}")
                    .ConfigureAwait(false);
                if (!visitedImport.Ok)
                    return Result.Failure($"{path} imports {importedName}: {visitedImport.Error}");
            }

            ordered.Add(new SlangModule(ToModuleName(path), path, loaded.Value.Text));

            return Result.Success();
        }
    }
}
