using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Assets;
using Magic.Files;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// A module's text with the name it imports as, in the order it is loaded: what one compile is made from.
/// </summary>
internal readonly record struct SlangModule(string Name, string Path, string Source);

/// <summary>
/// What reflecting a struct found: its fields, or the integer constant asked of it, null when it declares none. Cached as
/// a compiled shader is, so a run whose shaders are all cached never starts the compiler.
/// </summary>
internal readonly record struct Reflection(ImmutableArray<ShaderField> Fields, long? Constant);

/// <summary>
/// Compiles Slang entries to SPIR-V: the entry's module and every module it imports, found as <see cref="Shader"/>
/// assets, go into a session in dependency order, and the entry comes out compiled and cached under a hash of the
/// sources, so an unchanged entry never compiles twice.
/// </summary>
public sealed partial class ShaderCompiler : IShaderCompiler, ICache<CompiledShader>, ICache<Reflection>, IDisposable
{
    private const string ShadersFolder = "Shaders";

    private const string Extension = ".slang";

    /// <summary>
    /// How long the compiler stays started once nothing asks it for anything: long enough to bridge the gaps between a
    /// cold load's compiles.
    /// </summary>
    private const int IdleMs = 10_000;

    private readonly AssetManager _assets;

    private readonly Caches _caches;

    private readonly SlangShaderCompilerSettings _settings;

    /// <summary>
    /// One compile at a time: the compiler's global session is not for several threads at once.
    /// </summary>
    private readonly SemaphoreSlim _compiling = new(1, 1);

    /// <summary>
    /// The run's <see cref="App.Lifetime"/>, which ends before the gem is disposed, so compiles still waiting for their
    /// turn give up.
    /// </summary>
    private readonly CancellationToken _lifetime;

    /// <summary>
    /// Releases the global session <see cref="IdleMs"/> after the last compile, each compile setting it off again: a
    /// debounce.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Dispose disposes it through Dispose(WaitHandle), which waits out a release still running."
    )]
    private readonly Timer _idle;

    /// <summary>
    /// The compiler, a few hundred megabytes: null until a compile needs it, and again once it has been idle. Read and
    /// written only while holding <see cref="_compiling"/>.
    /// </summary>
    private unsafe void* _globalSession;

    public ShaderCompiler(App app, AssetManager assets, Caches caches, SlangShaderCompilerSettings settings)
    {
        _assets = assets;
        _caches = caches;
        _settings = settings;
        _lifetime = app.Lifetime;
        _idle = new Timer(_ =>
        {
            // A compile that holds the session sets the timer off again as it ends.
            if (!_compiling.Wait(0))
                return;

            ReleaseGlobalSession();
            _compiling.Release();
        });
    }

    /// <summary>
    /// Raised when the bytes written change shape, and when the compiler's options or tag change.
    /// </summary>
    public int Version => 4;

    public void Dispose()
    {
        // The compile already running finishes, then the timer stops, a release it began finishing, before the session is
        // freed. Waiting for them is safe: the frame loop has ended, and neither needs an engine thread.
        _compiling.Wait();
        using (ManualResetEvent stopped = new(false))
        {
            _idle.Dispose(stopped);
            stopped.WaitOne();
        }

        ReleaseGlobalSession();
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

        // Read once, so the compile is made and cached at the same level.
        ShaderOptimization optimization = _settings.ShaderOptimization;
        ulong key = GetKey(entryPath, entry, typeArguments, optimization);
        ulong version = GetVersion(modules.Value);
        Result<CompiledShader> cached = await _caches
            .ReadAsync<CompiledShader>(key, version, this, cancel)
            .ConfigureAwait(false);
        if (cached.Ok)
            return cached;

        Result<CompiledShader> compiled = await RunAsync(
                () => Compile(modules.Value, entryPath, entry, typeArguments, optimization),
                cancel
            )
            .ConfigureAwait(false);
        if (!compiled.Ok)
            return Result.Failure($"{entryPath}: {entry.Name} did not compile: {compiled.Error}");

        await _caches.WriteAsync(key, version, compiled.Value, this, cancel).ConfigureAwait(false);

        return compiled;
    }

    /// <summary>
    /// The fields of the struct <paramref name="type"/> names, as a buffer lays them out; or why not.
    /// </summary>
    public async Task<Result<ImmutableArray<ShaderField>>> ReflectAsync(
        ShaderTypeArgument type,
        CancellationToken cancel
    )
    {
        Result<Reflection> reflected = await ReflectAsync(type, string.Empty, ReadFields, cancel).ConfigureAwait(false);

        return reflected.Ok ? Result.Success(reflected.Value.Fields) : Result.Failure(reflected.Error);
    }

    /// <summary>
    /// The integer constant the struct <paramref name="type"/> names declares under <paramref name="name"/>; or why
    /// not.
    /// </summary>
    public async Task<Result<long>> ReflectConstantAsync(ShaderTypeArgument type, string name, CancellationToken cancel)
    {
        Result<Reflection> reflected = await ReflectAsync(
                type,
                name,
                (layout, found) => ReadConstant(layout, found, name),
                cancel
            )
            .ConfigureAwait(false);
        if (!reflected.Ok)
            return Result.Failure(reflected.Error);

        return reflected.Value.Constant is { } value
            ? Result.Success(value)
            : Result.Failure($"{type.Type} declares no integer constant {name}.");
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

    [GeneratedRegex(@"^\s*(?:__exported\s+)?import\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    /// <summary>
    /// <paramref name="text"/> as its length in bytes, then its UTF-8.
    /// </summary>
    private static void WriteText(IBufferWriter<byte> bytes, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.GetSpan(4), utf8.Length);
        bytes.Advance(4);
        bytes.Write(utf8);
    }

    /// <summary>
    /// What <see cref="WriteText"/> wrote at the start of <paramref name="bytes"/>, which moves past it.
    /// </summary>
    private static string ReadText(ref ReadOnlySpan<byte> bytes)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        string text = Encoding.UTF8.GetString(bytes.Slice(4, length));
        bytes = bytes[(4 + length)..];

        return text;
    }

    /// <summary>
    /// What names one compile: the entry, its stage, its type arguments and the optimization level, each level cached
    /// apart.
    /// </summary>
    private static ulong GetKey(
        string entryPath,
        in ShaderEntry entry,
        List<(string Path, string Type)> typeArguments,
        ShaderOptimization optimization
    )
    {
        XxHash64 hash = new();
        hash.Append(Encoding.UTF8.GetBytes(entryPath));
        hash.Append(Encoding.UTF8.GetBytes(entry.Name));
        hash.Append(BitConverter.GetBytes((int)entry.Stage));
        hash.Append(BitConverter.GetBytes((int)optimization));
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

    private static Slang.CompilerOptionEntry CreateOption(int name, int value) =>
        new()
        {
            Name = name,
            Value = new Slang.CompilerOptionValue { Kind = Slang.Target.OptionKindInt, IntValue0 = value },
        };

    private static unsafe Slang.CompilerOptionEntry CreateOption(int name, byte* value) =>
        new()
        {
            Name = name,
            Value = new Slang.CompilerOptionValue { Kind = Slang.Target.OptionKindString, StringValue0 = value },
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
    /// The fields of the struct <paramref name="type"/> in <paramref name="layout"/>, as a buffer lays them out: each
    /// one's name, offset and size, and a struct field's type's name.
    /// </summary>
    private static unsafe Reflection ReadFields(nint layout, nint type)
    {
        // Slang's SlangTypeKind of a struct, and its SlangParameterCategory of a field's bytes.
        const int Struct = 1;
        const int Uniform = 8;
        void* typeLayout = Slang.spReflection_GetTypeLayout((void*)layout, (void*)type, 0);
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

        return new Reflection(fields.MoveToImmutable(), null);
    }

    /// <summary>
    /// The value of the integer constant <paramref name="name"/> the struct <paramref name="type"/> declares, a
    /// <c>static const</c> whose initialiser is a literal; null when it declares none.
    /// </summary>
    private static unsafe Reflection ReadConstant(nint layout, nint type, string name)
    {
        void* variable;
        fixed (byte* named = CString.ToBytes(name))
        {
            variable = Slang.spReflection_FindVarByNameInType((void*)layout, (void*)type, named);
        }

        long value;

        return new Reflection(
            [],
            variable != null && Slang.spReflectionVariable_GetDefaultValueInt(variable, &value) >= 0 ? value : null
        );
    }

    void ICache<Reflection>.Write(Reflection value, IBufferWriter<byte> bytes)
    {
        // Whether the constant is declared and its value, the number of fields, then each field.
        Span<byte> header = bytes.GetSpan(13);
        header[0] = value.Constant is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt64LittleEndian(header[1..], value.Constant ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[9..], value.Fields.Length);
        bytes.Advance(13);
        foreach (ShaderField field in value.Fields)
        {
            Span<byte> sizes = bytes.GetSpan(8);
            BinaryPrimitives.WriteUInt32LittleEndian(sizes, field.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(sizes[4..], field.Size);
            bytes.Advance(8);
            WriteText(bytes, field.Name);
            WriteText(bytes, field.Struct);
        }
    }

    Result<Reflection> ICache<Reflection>.Read(ReadOnlySpan<byte> bytes)
    {
        // Bytes cut short throw from a slice, which the caches take as a dirty entry.
        long? constant = bytes[0] == 0 ? null : BinaryPrimitives.ReadInt64LittleEndian(bytes[1..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[9..]);
        bytes = bytes[13..];
        ImmutableArray<ShaderField>.Builder fields = ImmutableArray.CreateBuilder<ShaderField>(count);
        for (int i = 0; i < count; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            bytes = bytes[8..];
            string name = ReadText(ref bytes);
            fields.Add(new ShaderField(name, offset, size, ReadText(ref bytes)));
        }

        return Result.Success(new Reflection(fields.MoveToImmutable(), constant));
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
        List<(string Path, string Type)> typeArguments,
        ShaderOptimization optimization
    )
    {
        List<nint> owned = [];
        void* diagnostics = null;
        string messages;
        int result;
        try
        {
            Result<nint> made = CreateSession(optimization);
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
                    _idle.Change(IdleMs, Timeout.Infinite);
                    _compiling.Release();
                }
            },
            cancel
        );

    /// <summary>
    /// The session: SPIR-V 1.6, row-major, entry names kept, warnings as errors, buffers laid out scalar, as C# packs a
    /// struct and the heap reads one, and <paramref name="optimization"/>. A change here, but to the level, raises
    /// Version. It starts the compiler when it isn't running.
    /// </summary>
    private unsafe Result<nint> CreateSession(ShaderOptimization optimization)
    {
        if (_globalSession == null)
            _globalSession = Slang.CreateGlobalSession();
        if (_globalSession == null)
            return Result.Failure("the Slang compiler could not start.");

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
            options[0] = CreateOption(Slang.Option.EmitSpirvDirectly, 1);
            options[1] = CreateOption(Slang.Option.VulkanUseEntryPointName, 1);
            options[2] = CreateOption(Slang.Option.MatrixLayoutRow, 1);
            options[3] = CreateOption(Slang.Option.DefaultImageFormatUnknown, 1);
            options[4] = CreateOption(Slang.Option.Optimization, (int)optimization);
            options[5] = CreateOption(Slang.Option.WarningsAsErrors, all);
            // The heap's image queries need capabilities past the bare profile, which the compiler adds per entry:
            // a warning on every entry that every GPU Magic runs on has no need of.
            options[6] = CreateOption(Slang.Option.DisableWarning, profileUpgrade);
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
    /// What <paramref name="read"/> reads of the struct <paramref name="type"/> names, in a session of its module and
    /// what it imports, cached under the struct and the <paramref name="constant"/> asked of it, empty for its fields;
    /// or why not.
    /// </summary>
    private async Task<Result<Reflection>> ReflectAsync(
        ShaderTypeArgument type,
        string constant,
        Func<nint, nint, Reflection> read,
        CancellationToken cancel
    )
    {
        if (_assets.GetPath(type.Module) is not { } path)
            return Result.Failure($"the shader {type.Module.Id} is not an asset.");

        Result<ImmutableArray<SlangModule>> modules = await LoadModulesAsync([path], cancel).ConfigureAwait(false);
        if (!modules.Ok)
            return Result.Failure(modules.Error);

        ulong key = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"{path}\n{type.Type}\n{constant}"));
        ulong version = GetVersion(modules.Value);
        Result<Reflection> cached = await _caches
            .ReadAsync<Reflection>(key, version, this, cancel)
            .ConfigureAwait(false);
        if (cached.Ok)
            return cached;

        Result<Reflection> reflected = await RunAsync(() => Reflect(modules.Value, path, type.Type, read), cancel)
            .ConfigureAwait(false);
        if (reflected.Ok)
            await _caches.WriteAsync(key, version, reflected.Value, this, cancel).ConfigureAwait(false);

        return reflected;
    }

    /// <summary>
    /// What <paramref name="read"/> reads of <paramref name="typeName"/> in the module at <paramref name="typePath"/>,
    /// handed the session's layout and the type, both alive until it returns.
    /// </summary>
    private unsafe Result<Reflection> Reflect(
        ImmutableArray<SlangModule> modules,
        string typePath,
        string typeName,
        Func<nint, nint, Reflection> read
    )
    {
        // A struct's layout is the same at any level, so reflection compiles at the quickest.
        Result<nint> made = CreateSession(ShaderOptimization.None);
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

            return Result.Success(read((nint)layout, (nint)type));
        }
        finally
        {
            Slang.Release(session);
        }
    }

    /// <summary>
    /// The compiler stopped, its memory given back, for the next compile to start again; called holding
    /// <see cref="_compiling"/>.
    /// </summary>
    private unsafe void ReleaseGlobalSession()
    {
        if (_globalSession == null)
            return;

        Slang.Release(_globalSession);
        _globalSession = null;
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

            // Shaders/Core/Heap.slang as the compiler names it, Core/Heap, which is what import Core.Heap; asks for.
            string name = path.StartsWith(ShadersFolder + "/", StringComparison.Ordinal)
                ? path[(ShadersFolder.Length + 1)..]
                : path;
            if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                name = name[..^Extension.Length];

            ordered.Add(new SlangModule(name, path, loaded.Value.Text));

            return Result.Success();
        }
    }
}
