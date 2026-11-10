using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// Where one gem's assemblies load: the gem and its NuGet and native dependencies, from its folder. Magic and the
/// framework come from the host, so every gem shares one <see cref="IGem"/>, and two gems can carry different versions
/// of one package. Another listed gem it references comes from that gem's own context, so both see one set of its
/// types. A native library is found through the gem's dependency file, else under its
/// <c>runtimes/&lt;rid&gt;/native/</c>. Managed assemblies are read into memory, so a build can replace their files while
/// the gem runs. It is collectible: it unloads once nothing refers to the gem's types, or once
/// <see cref="AssemblyLoadContext.Unload"/> is called, and its assemblies are freed after that.
/// </summary>
internal sealed class GemLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Native libraries by name, loaded once for every gem and never freed: two copies would each keep their own state,
    /// so a window one opened would mean nothing to the other.
    /// </summary>
    private static readonly Dictionary<string, nint> _natives = new(StringComparer.Ordinal);
    private static readonly Lock _loadingNative = new();

    private readonly string _gemPath;
    private readonly IFileSystem _files;
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// The contexts of every gem the project lists, this one's among them, by the gems' names.
    /// </summary>
    private readonly IReadOnlyDictionary<string, GemLoadContext> _gems;

    internal GemLoadContext(string gemPath, IFileSystem files, IReadOnlyDictionary<string, GemLoadContext> gems)
        : base(Path.GetFileNameWithoutExtension(gemPath), isCollectible: true)
    {
        _gemPath = gemPath;
        _files = files;
        _gems = gems;
        _resolver = new AssemblyDependencyResolver(gemPath);

        // A static method, so the handler keeps no reference to the gem.
        Unloading += LogUnloaded;
    }

    /// <summary>
    /// The gem's own assembly, loaded the first time it is asked for, by the gem or a gem that references it; or why its
    /// file could not be read.
    /// </summary>
    internal Result<Assembly> LoadGem()
    {
        foreach (Assembly loaded in Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, Name, StringComparison.OrdinalIgnoreCase))
                return Result.Success(loaded);
        }

        return LoadFromBytes(_gemPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && _gems.TryGetValue(name, out GemLoadContext? gem))
            return Resolve(gem.LoadGem(), name);

        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? Resolve(LoadFromBytes(path), path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        lock (_loadingNative)
        {
            if (_natives.TryGetValue(unmanagedDllName, out nint loaded))
                return loaded;

            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) ?? FindCarried(unmanagedDllName);
            if (path is null)
                return 0;

            Log.Verbose($"Native library {unmanagedDllName} loads from {path}.");
            nint handle = LoadUnmanagedDllFromPath(path);
            _natives[unmanagedDllName] = handle;

            return handle;
        }
    }

    private static void LogUnloaded(AssemblyLoadContext context) => Log.Info($"Gem {context.Name} unloaded.");

    /// <summary>
    /// The assembly <paramref name="loaded"/> holds, or null when it failed, which is logged with <paramref name="what"/>
    /// it is.
    /// </summary>
    private static Assembly? Resolve(Result<Assembly> loaded, string what)
    {
        if (loaded.Ok)
            return loaded.Value;

        Log.Error($"{what} could not be loaded: {loaded.Error}");

        return null;
    }

    /// <summary>
    /// The assembly at <paramref name="path"/>, with its symbols when they are beside it, read into memory; or why its file
    /// could not be read. Read rather than loaded by path, since <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/>
    /// maps the file and locks it on Windows while the gem runs, so a build could not write it and the run could not
    /// restart on the new code. The symbols keep the files and lines in the gem's stack traces.
    /// </summary>
    private Result<Assembly> LoadFromBytes(string path)
    {
        Result<byte[]> assembly = _files.ReadBytes(path);
        if (!assembly.Ok)
            return Result.Failure(assembly.Error);

        using MemoryStream image = new(assembly.Value);
        Result<byte[]> symbols = _files.ReadBytes(Path.ChangeExtension(path, ".pdb"));
        if (!symbols.Ok)
            return Result.Success(LoadFromStream(image));

        using MemoryStream pdb = new(symbols.Value);

        return Result.Success(LoadFromStream(image, pdb));
    }

    /// <summary>
    /// The file under the gem's <c>runtimes/&lt;rid&gt;/native/</c> named like the library, with or without Unix's
    /// <c>lib</c> prefix; null when there is none.
    /// </summary>
    private string? FindCarried(string name)
    {
        string folder = _files.Combine(
            _files.GetParent(_gemPath),
            "runtimes",
            RuntimeInformation.RuntimeIdentifier,
            "native"
        );
        if (!_files.DirectoryExists(folder))
            return null;

        Result<string[]> found = _files.EnumerateFiles(folder, "*", recursive: false);
        if (!found.Ok)
            return null;

        foreach (string file in found.Value)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (
                string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "lib" + name, StringComparison.OrdinalIgnoreCase)
            )
            {
                return file;
            }
        }

        return null;
    }
}
