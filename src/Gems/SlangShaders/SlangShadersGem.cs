using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Caching;
using Magic.Gems;
using Magic.Shaders;
using Magic.Utils;

namespace SlangShaders;

/// <summary>
/// Compiles Slang entries to SPIR-V: the entry's module and every module it imports, found as <see cref="Shader"/>
/// assets, go into a session in dependency order, and the entry comes out compiled and cached under a hash of the
/// sources and the compiler's options, so an unchanged entry never compiles twice. It is public because nothing in its
/// assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed partial class SlangShadersGem : IGem, IShaderCompiler, ICache<CompiledShader>, IDisposable
{
    private const string ShadersFolder = "Shaders";
    private const string Extension = ".slang";

    private readonly AssetDatabase _assets;
    private readonly Caches _caches;

    /// <summary>
    /// One compile at a time: the compiler's global session is not for several threads at once.
    /// </summary>
    private readonly SemaphoreSlim _compiling = new(1, 1);

    private readonly nint _globalSession;

    public SlangShadersGem(AssetDatabase assets, Caches caches)
    {
        _assets = assets;
        _caches = caches;
        _globalSession = Slang.CreateGlobalSession();
        if (_globalSession == 0)
            throw new InvalidOperationException("The Slang compiler could not start.");
    }

    /// <summary>
    /// Raised when the bytes written change shape.
    /// </summary>
    public int Version => 1;

    public void Dispose()
    {
        Slang.Release(_globalSession);
        Slang.Shutdown();
        _compiling.Dispose();
    }

    public async Task<Result<CompiledShader>> CompileAsync(
        ShaderEntry entry,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string? entryPath = _assets.GetPath(entry.Module);
        if (entryPath is null)
            return Result.Failure($"the shader {entry.Module.Id} is not an asset.");

        string work = $"Compiling {entryPath}: {entry.Name}";
        progress?.Report(new LoadProgress(work, 0, 3));
        Result<ImmutableArray<SlangModule>> modules = await LoadModulesAsync(entry, cancel).ConfigureAwait(false);
        if (!modules.Ok)
            return Result.Failure(modules.Error);

        progress?.Report(new LoadProgress(work, 1, 3));
        ImmutableArray<(string Module, string Type)> typeArguments = GetTypeArguments(entry);
        ulong key = GetKey(entryPath, entry, typeArguments);
        ulong version = GetVersion(modules.Value);
        Result<CompiledShader> cached = await _caches.ReadAsync(key, version, this, cancel).ConfigureAwait(false);
        if (cached.Ok)
        {
            progress?.Report(new LoadProgress(work, 3, 3));

            return cached;
        }

        progress?.Report(new LoadProgress(work, 2, 3));
        Result<CompiledShader> compiled = await Task.Run(
                async () =>
                {
                    await _compiling.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        return SlangCompile.Compile(
                            _globalSession,
                            modules.Value,
                            ToModuleName(entryPath),
                            entry,
                            typeArguments
                        );
                    }
                    finally
                    {
                        _compiling.Release();
                    }
                },
                cancel
            )
            .ConfigureAwait(false);
        progress?.Report(new LoadProgress(work, 3, 3));
        if (!compiled.Ok)
            return Result.Failure($"{entryPath}: {entry.Name} did not compile: {compiled.Error}");

        await _caches.WriteAsync(key, version, compiled.Value, this, cancel).ConfigureAwait(false);

        return compiled;
    }

    public void Write(CompiledShader value, IBufferWriter<byte> bytes)
    {
        byte[] name = Encoding.UTF8.GetBytes(value.EntryPoint);
        Span<byte> header = bytes.GetSpan(20);
        BinaryPrimitives.WriteInt32LittleEndian(header, (int)value.Stage);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], value.GroupSizeX);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], value.GroupSizeY);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], value.GroupSizeZ);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], name.Length);
        bytes.Advance(20);
        bytes.Write(name);
        Span<byte> length = bytes.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Spirv.Length);
        bytes.Advance(4);
        bytes.Write(value.Spirv.Span);
    }

    public Result<CompiledShader> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24)
            return Result.Failure("the entry is cut short.");

        int stage = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        uint x = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint y = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint z = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        bytes = bytes[20..];
        if (nameLength < 0 || bytes.Length < nameLength + 4)
            return Result.Failure("the entry is cut short.");

        string name = Encoding.UTF8.GetString(bytes[..nameLength]);
        bytes = bytes[nameLength..];
        int codeLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        bytes = bytes[4..];
        if (codeLength < 0 || bytes.Length < codeLength)
            return Result.Failure("the entry is cut short.");

        return Result.Success(
            new CompiledShader
            {
                Spirv = bytes[..codeLength].ToArray(),
                EntryPoint = name,
                Stage = (ShaderStage)stage,
                GroupSizeX = x,
                GroupSizeY = y,
                GroupSizeZ = z,
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

    /// <summary>
    /// <c>Core.Heap</c> as its file is found, <c>Shaders/Core/Heap.slang</c>.
    /// </summary>
    private static string ToPath(string moduleName) => $"{ShadersFolder}/{moduleName.Replace('.', '/')}{Extension}";

    [GeneratedRegex(@"^\s*(?:__exported\s+)?import\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    private static ulong GetKey(
        string entryPath,
        in ShaderEntry entry,
        ImmutableArray<(string Module, string Type)> typeArguments
    )
    {
        XxHash64 hash = new();
        hash.Append(Encoding.UTF8.GetBytes(entryPath));
        hash.Append(Encoding.UTF8.GetBytes(entry.Name));
        hash.Append(BitConverter.GetBytes((int)entry.Stage));
        foreach ((string module, string type) in typeArguments)
        {
            hash.Append(Encoding.UTF8.GetBytes(module));
            hash.Append(Encoding.UTF8.GetBytes(type));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// What the compile is made from: every module's text, in order, and the compiler's options.
    /// </summary>
    private static ulong GetVersion(ImmutableArray<SlangModule> modules)
    {
        XxHash64 hash = new();
        foreach (SlangModule module in modules)
        {
            hash.Append(Encoding.UTF8.GetBytes(module.Name));
            hash.Append(Encoding.UTF8.GetBytes(module.Source));
        }

        foreach (string option in SlangCompile.Options)
        {
            hash.Append(Encoding.UTF8.GetBytes(option));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Each type argument as the module's name and the type's, as the compile composes and names them.
    /// </summary>
    private ImmutableArray<(string Module, string Type)> GetTypeArguments(in ShaderEntry entry)
    {
        ImmutableArray<(string, string)>.Builder arguments = ImmutableArray.CreateBuilder<(string, string)>(
            entry.TypeArguments.Length
        );
        foreach (ShaderTypeArgument argument in entry.TypeArguments)
        {
            string? path = _assets.GetPath(argument.Module);
            arguments.Add((path is null ? string.Empty : ToModuleName(path), argument.Type));
        }

        return arguments.MoveToImmutable();
    }

    /// <summary>
    /// The entry's module, the modules its type arguments are in, and everything they import, each once, imports
    /// before what imports them.
    /// </summary>
    private async Task<Result<ImmutableArray<SlangModule>>> LoadModulesAsync(
        ShaderEntry entry,
        CancellationToken cancel
    )
    {
        List<SlangModule> ordered = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        List<Handle<Shader>> roots = [entry.Module];
        foreach (ShaderTypeArgument argument in entry.TypeArguments)
        {
            roots.Add(argument.Module);
        }

        foreach (Handle<Shader> root in roots)
        {
            string? path = _assets.GetPath(root);
            if (path is null)
                return Result.Failure($"the shader {root.Id} is not an asset.");

            Result visited1 = await VisitAsync(root, path, ordered, visited, cancel).ConfigureAwait(false);
            if (!visited1.Ok)
                return Result.Failure(visited1.Error);
        }

        return Result.Success(ordered.ToImmutableArray());
    }

    private async Task<Result> VisitAsync(
        Handle<Shader> handle,
        string path,
        List<SlangModule> ordered,
        HashSet<string> visited,
        CancellationToken cancel
    )
    {
        string name = ToModuleName(path);
        if (!visited.Add(name))
            return Result.Success();

        Result<Shader> loaded = await _assets.LoadAsync(handle, null, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
            return Result.Failure($"{path} could not be read: {loaded.Error}");

        foreach (Match import in ImportLine().Matches(loaded.Value.Text))
        {
            string importedName = import.Groups[1].Value.Replace('.', '/');
            string importedPath = ToPath(importedName);
            Handle<Shader> imported = _assets.Find<Shader>(importedPath);
            if (imported == Handle<Shader>.None)
                return Result.Failure($"{path} imports {importedName}, and there is no {importedPath}.");

            Result visitedImport = await VisitAsync(imported, importedPath, ordered, visited, cancel)
                .ConfigureAwait(false);
            if (!visitedImport.Ok)
                return visitedImport;
        }

        ordered.Add(new SlangModule(name, path, loaded.Value.Text));

        return Result.Success();
    }
}
