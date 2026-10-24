using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using static Magic.SlangShaderCompiler.Slang;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// <see cref="IShaderCompiler"/> on Khronos's Slang, called directly so every option is the engine's: Slang 2026,
/// SPIR-V 1.3 for Vulkan 1.1, whose subgroup operations are wave intrinsics, buffers laid out scalar, matrices
/// row-major, optimised, and warnings as errors. What a shader takes is read from Slang's reflection
/// (<see cref="ShaderReflection"/>, <see cref="StructReflection"/>). The modules a program is made of, and every module
/// they import, are loaded as assets before Slang starts and read through a file system over them
/// (<see cref="SourceFileSystem"/>). What it compiles is kept in the cache (<see cref="Caches"/>, which it writes for
/// as <see cref="ICache{T}"/> of <see cref="GpuShader"/>): named by its entry point, the types it is specialized by,
/// its format and every module it reads, so no two programs share an entry, and made with the debug-info setting, so a
/// shader compiles once, and again only when a module it reads or the setting changes. Each compile takes a global
/// session no other compile holds (<see cref="GlobalSessions"/>), so it is safe from any thread; a program asked for
/// while it already compiles waits for that compile rather than starting its own, so each compiles, and its entry is
/// written, once.
/// </summary>
internal sealed partial class SlangShaderCompilerGem(
    Assets assets,
    Caches caches,
    SlangShaderCompilerSettings settings) : IGem, IShaderCompiler, ICache<GpuShader>, IDisposable
{
    /// <summary>
    /// Where every shader module's asset is: an import names it by its path under this.
    /// </summary>
    private const string ShaderFolder = "Shaders/";

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Core's, which Core ends after every gem.")]
    private readonly Assets _assets = assets;
    private readonly Caches _caches = caches;
    private readonly SlangShaderCompilerSettings _settings = settings;
    private readonly GlobalSessions _globalSessions = new();

    /// <summary>
    /// Whether the shaders were last compiled with debug info, so a change while running compiles them again.
    /// </summary>
    private bool _debugInfo = settings.HasDebugInfo;

    /// <summary>
    /// The compiles on their way, by cache key, for a program asked for again while it compiles to wait for.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, Task<Result<GpuShader>>> _compiling = new();

    /// <summary>
    /// Raised whenever Slang's options change, or how a shader is written to the cache, so what was compiled before is
    /// compiled again.
    /// </summary>
    public int Version => 3;

    public void Dispose() => _globalSessions.Dispose();

    /// <summary>
    /// Compiles every shader again when debug info was turned on or off while running.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (_settings.HasDebugInfo == _debugInfo) return;

        _debugInfo = _settings.HasDebugInfo;
        _assets.RecreateAsync<Shader>().FireAndForget();
    }

    /// <summary>
    /// The code's length, then what <see cref="GpuShader"/> declares in its order, the code's bytes last.
    /// </summary>
    public void Write(GpuShader value, Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Code.Length);
        writer.Write(value.EntryPoint);
        writer.Write((int)value.Stage);
        writer.Write(value.ConstantBytes);
        writer.Write(value.ThreadCountX);
        writer.Write(value.ThreadCountY);
        writer.Write(value.ThreadCountZ);
        writer.Write(value.Code.Span);
    }

    public Result<GpuShader> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        int length = reader.ReadInt32();
        string entryPoint = reader.ReadString();
        GpuStage stage = (GpuStage)reader.ReadInt32();
        uint constantBytes = reader.ReadUInt32();
        uint threadsX = reader.ReadUInt32();
        uint threadsY = reader.ReadUInt32();
        uint threadsZ = reader.ReadUInt32();
        GpuShader shader = new()
        {
            EntryPoint = entryPoint,
            Stage = stage,
            ConstantBytes = constantBytes,
            ThreadCountX = threadsX,
            ThreadCountY = threadsY,
            ThreadCountZ = threadsZ,
            Code = reader.ReadBytes(length),
        };

        return shader.Code.Length == length
            ? Result.Success(shader)
            : Result.Failure<GpuShader>("its code is cut short.");
    }

    public async Task<Result<GpuShader>> CompileAsync(
        ShaderEntry entry,
        GpuShaderFormat format,
        CancellationToken cancel)
    {
        if (format != GpuShaderFormat.Spirv)
            throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such shader format.");

        Result<Program> read = await ReadProgramAsync(entry, cancel).ConfigureAwait(false);

        if (!read.Ok) return Result.Failure<GpuShader>(read.Error);

        // A compile of the same key already on its way is waited for rather than started again.
        Program program = read.Value;
        ulong key = GetKey(program, format);
        TaskCompletionSource<Result<GpuShader>> own = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<GpuShader>> compiling = _compiling.GetOrAdd(key, own.Task);

        if (compiling != own.Task) return await compiling.WaitAsync(cancel).ConfigureAwait(false);

        try
        {
            Result<GpuShader> made = await ReadOrCompileAsync(key, program).ConfigureAwait(false);
            own.SetResult(made);

            return made;
        }
        catch (Exception ex)
        {
            own.SetException(ex);

            throw;
        }
        finally
        {
            _compiling.TryRemove(new(key, own.Task));
        }
    }

    public async Task<Result<ShaderStruct>> ReflectStructAsync(ShaderType type, CancellationToken cancel)
    {
        Result<Program> read = await ReadProgramAsync(new ShaderEntry(type.Module), cancel).ConfigureAwait(false);

        return read.Ok
            ? await Task.Run(() => Reflect(read.Value, type.Name), cancel).ConfigureAwait(false)
            : Result.Failure<ShaderStruct>(read.Error);
    }

    [GeneratedRegex(
        @"^\s*(__exported\s+)?import\s+(?<module>[\w.]+)\s*;",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Import();

    /// <summary>
    /// The cache key of a compile: its entry point, the types it is specialized by and its format, and every module it
    /// reads with its path, in path order.
    /// </summary>
    private static ulong GetKey(Program program, GpuShaderFormat format)
    {
        XxHash3 hash = new();
        string types = string.Join(",", program.Types.Select(type => $"{type.Module}:{type.Name}"));
        string modules = string.Join(",", program.Modules);
        hash.Append(Encoding.UTF8.GetBytes($"{program.Module}\n{program.EntryPoint}\n{types}\n{modules}\n{format}"));
        foreach (string path in program.Sources.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.Append(Encoding.UTF8.GetBytes($"\n{path}\n{program.Sources[path]}"));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// What Slang <paramref name="said"/> of a step that failed, else <paramref name="otherwise"/>.
    /// </summary>
    private static string ReadFailure(Program program, nint said, string otherwise)
    {
        string text = said == 0 ? string.Empty : Encoding.UTF8.GetString(GetBytes(said)).TrimEnd('\0', '\n', ' ');

        return text.Length > 0 ? text : $"{program.Module}: {otherwise}";
    }

    /// <summary>
    /// The modules a program is made of, its entry point's and those that declare the types it is specialized by, and
    /// every module they import, loaded; why not, when one is not a shader under <c>Shaders/</c> or does not load.
    /// </summary>
    private async Task<Result<Program>> ReadProgramAsync(ShaderEntry entry, CancellationToken cancel)
    {
        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        Result<string> module = await ReadModuleAsync(entry.Module, sources, cancel).ConfigureAwait(false);

        if (!module.Ok) return Result.Failure<Program>(module.Error);

        List<ProgramType> types = [];
        foreach (ShaderType type in entry.Types)
        {
            string? typeModule = null;
            if (type.Module != Handle<Shader>.None)
            {
                Result<string> read = await ReadModuleAsync(type.Module, sources, cancel).ConfigureAwait(false);

                if (!read.Ok) return Result.Failure<Program>(read.Error);

                typeModule = read.Value;
            }

            types.Add(new ProgramType(typeModule, type.Name));
        }

        List<string> modules = [];
        foreach (Handle<Shader> extra in entry.Modules)
        {
            Result<string> read = await ReadModuleAsync(extra, sources, cancel).ConfigureAwait(false);

            if (!read.Ok) return Result.Failure<Program>(read.Error);

            modules.Add(read.Value);
        }

        return Result.Success(new Program(module.Value, entry.EntryPoint, types, modules, sources));
    }

    /// <summary>
    /// Adds <paramref name="shader"/>'s source and every module it imports to <paramref name="sources"/>, and hands
    /// back its module's name.
    /// </summary>
    private async Task<Result<string>> ReadModuleAsync(
        Handle<Shader> shader,
        Dictionary<string, string> sources,
        CancellationToken cancel)
    {
        string? path = _assets.GetPath(shader);

        if (path?.StartsWith(ShaderFolder, StringComparison.OrdinalIgnoreCase) != true)
            return Result.Failure<string>($"{path ?? $"shader {shader.Id}"} is no module: modules are under Shaders/.");

        Result<Shader> loaded = await _assets.LoadAsync(shader, cancel).ConfigureAwait(false);

        if (!loaded.Ok) return Result.Failure<string>($"{path}: {loaded.Error}");

        string file = path[ShaderFolder.Length..];
        sources[file] = loaded.Value.Text;
        Result read = await ReadImportsAsync(loaded.Value.Text, path, sources, cancel).ConfigureAwait(false);

        return read.Ok ? Result.Success(file) : Result.Failure<string>(read.Error);
    }

    /// <summary>
    /// What the cache holds of <paramref name="key"/>, made with the debug-info setting as it is now, else
    /// <paramref name="program"/> compiled and kept there. Not cancelled: a compile is short and what it makes is kept,
    /// and whoever else waits for it stops waiting on their own.
    /// </summary>
    private async Task<Result<GpuShader>> ReadOrCompileAsync(ulong key, Program program)
    {
        // An entry made with debug info is another version than one made without, so changing the setting finds the
        // entries dirty.
        ulong version = _settings.HasDebugInfo ? 1UL : 0UL;

        if (await _caches.ReadAsync<GpuShader>(key, version, CancellationToken.None)
            .ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached;
        }

        Result<GpuShader> compiled = await Task.Run(() => Compile(program)).ConfigureAwait(false);
        if (compiled.Ok)
            await _caches.WriteAsync(key, version, compiled.Value, CancellationToken.None).ConfigureAwait(false);

        return compiled;
    }

    /// <summary>
    /// Adds to <paramref name="sources"/> every module <paramref name="source"/> imports that it lacks, and every
    /// module those import in turn: <c>import A.B</c> is the shader asset at <c>Shaders/A/B.slang</c>, so Slang needs
    /// no folder to find them in and a shader imports whatever shaders are assets, whichever root they are under. One
    /// no asset is at is left out, for Slang to report; one that does not load fails the compile.
    /// <paramref name="name"/> is what <paramref name="source"/> is called.
    /// </summary>
    private async Task<Result> ReadImportsAsync(
        string source,
        string name,
        Dictionary<string, string> sources,
        CancellationToken cancel)
    {
        foreach (Match import in Import().Matches(source))
        {
            string path = import.Groups["module"].Value.Replace('.', '/') + ".slang";
            Handle<Shader> asset = sources.ContainsKey(path)
                ? Handle<Shader>.None
                : _assets.Find<Shader>(ShaderFolder + path);

            if (asset == Handle<Shader>.None) continue;

            Result<Shader> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);

            if (!loaded.Ok) return Result.Failure($"{name}: {path}: {loaded.Error}");

            sources[path] = loaded.Value.Text;
            Result read = await ReadImportsAsync(loaded.Value.Text, path, sources, cancel).ConfigureAwait(false);

            if (!read.Ok) return read;
        }

        return Result.Success();
    }

    /// <summary>
    /// <paramref name="program"/> compiled by Slang to SPIR-V, with what it takes read from its reflection
    /// (<see cref="ShaderReflection"/>); a failure carries Slang's own text.
    /// </summary>
    private unsafe Result<GpuShader> Compile(Program program)
    {
        using Session session = new(this, program);

        if (session.Error is { } error) return Result.Failure<GpuShader>(error);

        nint module = session.LoadModule(program.Module);

        if (module == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang loaded nothing."));

        nint entryPoint = session.FindEntryPoint(module, program.EntryPoint);

        if (entryPoint == 0) return Result.Failure<GpuShader>(session.ReadFailure(session.EntryPointError));

        // The module, the modules that declare the types it is specialized by and the entry point as one program,
        // specialized when the entry point is generic, then linked with what it imports.
        List<nint> parts = [module];
        foreach (string file in program.Types.Select(type => type.Module).OfType<string>().Concat(program.Modules))
        {
            nint loaded = session.LoadModule(file);

            if (loaded == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang loaded nothing."));

            if (!parts.Contains(loaded)) parts.Add(loaded);
        }

        parts.Add(entryPoint);
        nint composite = session.Compose(parts);

        if (composite == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang composed nothing."));

        nint specialized = program.Types.Count == 0 ? composite : session.Specialize(composite, program.Types);

        if (specialized == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang specialized nothing."));

        nint linked = session.Link(specialized);

        if (linked == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang linked nothing."));

        nint code = session.GetCode(linked);

        if (code == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang compiled nothing."));

        nint layout = session.GetLayout(linked);

        if (layout == 0) return Result.Failure<GpuShader>(session.ReadFailure("Slang read nothing of it."));

        Result<GpuShader> reflected = ShaderReflection.Read(layout, GetBytes(code).ToArray());

        return reflected.Ok ? reflected : Result.Failure<GpuShader>($"{program.Module}: {reflected.Error}");
    }

    /// <summary>
    /// The struct <paramref name="name"/> <paramref name="program"/>'s module declares, read from its reflection
    /// (<see cref="StructReflection"/>).
    /// </summary>
    private Result<ShaderStruct> Reflect(Program program, string name)
    {
        using Session session = new(this, program);

        if (session.Error is { } error) return Result.Failure<ShaderStruct>(error);

        nint module = session.LoadModule(program.Module);

        if (module == 0) return Result.Failure<ShaderStruct>(session.ReadFailure("Slang loaded nothing."));

        nint layout = session.GetLayout(module);

        if (layout == 0) return Result.Failure<ShaderStruct>(session.ReadFailure("Slang read nothing of it."));

        Result<ShaderStruct> reflected = StructReflection.Read(layout, name);

        return reflected.Ok ? reflected : Result.Failure<ShaderStruct>($"{program.Module}: {reflected.Error}");
    }

    /// <summary>
    /// A session that compiles for this GPU with the engine's options, reading files from <paramref name="fileSystem"/>
    /// from its root.
    /// </summary>
    private unsafe int CreateCompileSession(nint globalSession, nint fileSystem, nint* session)
    {
        // Slang 2026, optimised, with debug info when asked for, and warnings as errors but the one that says the
        // profile grew: SPIR-V 1.3 is the floor, and a shader takes the capabilities it uses on top, as its stage and
        // the heap need. What it reads and writes in buffers is laid out scalar, as C# packs a struct, so a row is the
        // same bytes on both sides.
        CompilerOptionEntry* options = stackalloc CompilerOptionEntry[]
        {
            CompilerOptionEntry.Integer(CompilerOptionName.LanguageVersion, LanguageVersion2026),
            CompilerOptionEntry.Integer(CompilerOptionName.Optimization, 2),
            CompilerOptionEntry.Integer(CompilerOptionName.DebugInformation, _settings.HasDebugInfo ? 2 : 0),
            CompilerOptionEntry.Text(CompilerOptionName.WarningsAsErrors, CString.Constant("all"u8)),
            CompilerOptionEntry.Text(CompilerOptionName.DisableWarning, CString.Constant("41012"u8)),
        };
        TargetDesc target = new()
        {
            StructureSize = (nuint)sizeof(TargetDesc),
            Format = Spirv,
            Profile = FindProfile(globalSession, CString.Constant("spirv_1_3"u8)),
            Flags = GenerateSpirvDirectly,
            ForceGlslScalarBufferLayout = true,
        };
        byte* root = CString.Constant("."u8);
        SessionDesc desc = new()
        {
            StructureSize = (nuint)sizeof(SessionDesc),
            Targets = &target,
            TargetCount = 1,
            DefaultMatrixLayoutMode = RowMajor,
            SearchPaths = &root,
            SearchPathCount = 1,
            FileSystem = fileSystem,
            CompilerOptionEntries = options,
            CompilerOptionEntryCount = 5,
        };

        return CreateSession(globalSession, &desc, session);
    }

    /// <summary>
    /// What one compile or reflection is made of: its entry point's module, by its file under Shaders/, as Slang loads
    /// it (Renderer/Sky.slang, which an import names Renderer.Sky), the entry point by name or none for the module's one,
    /// the types it is specialized by, the modules loaded beside it for them, and every module it reads by that path.
    /// </summary>
    private sealed record Program(
        string Module,
        string? EntryPoint,
        IReadOnlyList<ProgramType> Types,
        IReadOnlyList<string> Modules,
        Dictionary<string, string> Sources);

    /// <summary>
    /// A type a program is specialized by: the module that declares it, by its file under Shaders/, or none for one the
    /// entry point's module reaches; and its name.
    /// </summary>
    private sealed record ProgramType(string? Module, string Name);

    /// <summary>
    /// One Slang session over a program's sources, its global session taken from the pool and given back once it is
    /// disposed, and every object made in it released. What Slang said of each step is kept, so a step that fails reads
    /// what it said (<see cref="ReadFailure(string)"/>).
    /// </summary>
    private sealed unsafe class Session : IDisposable
    {
        /// <summary>
        /// The most steps a compile takes, each with what Slang said of it.
        /// </summary>
        private const int MaxSteps = 16;

        private readonly SlangShaderCompilerGem _compiler;
        private readonly Program _program;
        private readonly nint _globalSession;
        private readonly nint _fileSystem;
        private readonly nint _session;
        private readonly nint* _said = (nint*)NativeMemory.AllocZeroed(MaxSteps, (nuint)sizeof(nint));
        private readonly List<nint> _made = [];
        private int _steps;

        internal Session(SlangShaderCompilerGem compiler, Program program)
        {
            _compiler = compiler;
            _program = program;
            Result<nint> taken = compiler._globalSessions.Take();
            if (!taken.Ok)
            {
                Error = $"{program.Module}: {taken.Error}";

                return;
            }

            _globalSession = taken.Value;
            _fileSystem = SourceFileSystem.Create(program.Sources.ToDictionary(
                pair => pair.Key,
                pair => Encoding.UTF8.GetBytes(pair.Value),
                StringComparer.OrdinalIgnoreCase));
            nint session;
            int created = compiler.CreateCompileSession(_globalSession, _fileSystem, &session);
            _session = session;
            if (created < 0) Error = $"{program.Module}: Slang could not start (0x{created:X8}).";
        }

        /// <summary>
        /// Why the session could not start; none when it did.
        /// </summary>
        internal string? Error { get; }

        /// <summary>
        /// Why <see cref="FindEntryPoint"/> found none, when Slang said nothing.
        /// </summary>
        internal string EntryPointError => _program.EntryPoint is { } name
            ? $"it has no entry point {name}."
            : "it has no one entry point: name the one to compile.";

        public void Dispose()
        {
            for (int i = 0; i < _steps; i++)
            {
                Release(_said[i]);
            }

            NativeMemory.Free(_said);
            for (int i = _made.Count - 1; i >= 0; i--)
            {
                Release(_made[i]);
            }

            Release(_session);
            Release(_fileSystem);
            if (_globalSession != 0) _compiler._globalSessions.Return(_globalSession);
        }

        /// <summary>
        /// The module in <paramref name="file"/> under Shaders/, loaded with what it imports; none when it does not.
        /// </summary>
        internal nint LoadModule(string file)
        {
            fixed (byte* named = CString.ToBytes(file))
            {
                return Slang.LoadModule(_session, named, NextSaid());
            }
        }

        /// <summary>
        /// The entry point <paramref name="name"/> of <paramref name="module"/>, or its one entry point when none is
        /// named; none when there is no such one.
        /// </summary>
        internal nint FindEntryPoint(nint module, string? name)
        {
            nint entryPoint = 0;
            NextSaid();
            if (name is not null)
            {
                fixed (byte* named = CString.ToBytes(name))
                {
                    if (FindEntryPointByName(module, named, &entryPoint) < 0) return 0;
                }
            }
            else if (GetDefinedEntryPointCount(module) != 1 || GetDefinedEntryPoint(module, 0, &entryPoint) < 0)
            {
                return 0;
            }

            return Keep(entryPoint);
        }

        internal nint Compose(List<nint> parts)
        {
            nint* list = stackalloc nint[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                list[i] = parts[i];
            }

            nint composite;

            return CreateCompositeComponentType(_session, list, parts.Count, &composite, NextSaid()) < 0
                ? 0
                : Keep(composite);
        }

        /// <summary>
        /// <paramref name="composite"/> with its entry point's type parameters bound to <paramref name="types"/>, each
        /// found by name where the program sees it; none when one is not found or does not fit.
        /// </summary>
        internal nint Specialize(nint composite, IReadOnlyList<ProgramType> types)
        {
            nint layout = GetLayout(composite);

            if (layout == 0) return 0;

            SpecializationArg* arguments = stackalloc SpecializationArg[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                nint type;
                fixed (byte* named = CString.ToBytes(types[i].Name))
                {
                    type = spReflection_FindTypeByName(layout, named);
                }

                if (type == 0) return 0;

                arguments[i] = SpecializationArg.Of(type);
            }

            nint specialized;

            return Slang.Specialize(composite, arguments, types.Count, &specialized, NextSaid()) < 0
                ? 0
                : Keep(specialized);
        }

        internal nint Link(nint program)
        {
            nint linked;

            return Slang.Link(program, &linked, NextSaid()) < 0 ? 0 : Keep(linked);
        }

        /// <summary>
        /// The program's one entry point compiled for the session's target.
        /// </summary>
        internal nint GetCode(nint linked)
        {
            nint code;

            return GetEntryPointCode(linked, 0, 0, &code, NextSaid()) < 0 ? 0 : Keep(code);
        }

        /// <summary>
        /// The program's reflection, which the program keeps.
        /// </summary>
        internal nint GetLayout(nint program) => Slang.GetLayout(program, 0, NextSaid());

        /// <summary>
        /// What Slang said of the last step, else <paramref name="otherwise"/>.
        /// </summary>
        internal string ReadFailure(string otherwise)
            => SlangShaderCompilerGem.ReadFailure(_program, _steps > 0 ? _said[_steps - 1] : 0, otherwise);

        private nint* NextSaid()
        {
            if (_steps == MaxSteps) throw new InvalidOperationException($"A compile takes at most {MaxSteps} steps.");

            return &_said[_steps++];
        }

        private nint Keep(nint made)
        {
            _made.Add(made);

            return made;
        }
    }
}
