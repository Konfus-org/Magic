using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// Where the code of one gem or project loads from its folder: its assembly and its NuGet and native dependencies.
/// </summary>
/// <remarks>
/// <para>
/// Magic and the framework come from the host, so all gems share one <see cref="IGem"/>, and two gems can carry
/// different versions of one package. Other code of the run that it references, such as a listed gem, comes from that
/// code's own context, so both see one set of its types.
/// </para>
/// <para>
/// A native library is found through the dependency file, else under <c>runtimes/&lt;rid&gt;/native/</c>. Managed
/// assemblies are read into memory, so a build can replace their files while the code runs.
/// </para>
/// <para>
/// It is collectible: it unloads when nothing refers to its types, or after
/// <see cref="AssemblyLoadContext.Unload"/>.
/// </para>
/// </remarks>
internal sealed class CodeLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Native libraries by name, loaded one time for all gems and never freed.
    /// </summary>
    /// <remarks>
    /// Two copies would each keep their own state, so a window that one opened would mean nothing to the other.
    /// </remarks>
    private static readonly Dictionary<string, nint> _natives = new(StringComparer.Ordinal);
    private static readonly Lock _loadingNative = new();

    private readonly string _path;
    private readonly IFileSystem _files;
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// The contexts of all the code that the run can load, this one included, by assembly name.
    /// </summary>
    private readonly IReadOnlyDictionary<string, CodeLoadContext> _code;

    internal CodeLoadContext(string path, IFileSystem files, IReadOnlyDictionary<string, CodeLoadContext> code)
        : base(Path.GetFileNameWithoutExtension(path), isCollectible: true)
    {
        _path = path;
        _files = files;
        _code = code;
        _resolver = new AssemblyDependencyResolver(path);

        // A static method, so the handler keeps no reference to the code.
        Unloading += LogUnloaded;
    }

    /// <summary>
    /// Gets its own assembly, loaded on the first ask, or why its file could not be read.
    /// </summary>
    internal Result<Assembly> LoadCode()
    {
        foreach (Assembly loaded in Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, Name, StringComparison.OrdinalIgnoreCase))
                return Result.Success(loaded);
        }

        return LoadFromBytes(_path);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && _code.TryGetValue(name, out CodeLoadContext? code))
            return Resolve(code.LoadCode(), name);

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

    private static void LogUnloaded(AssemblyLoadContext context) => Log.Info($"{context.Name} unloaded.");

    /// <summary>
    /// Gets the assembly of <paramref name="loaded"/>, or null when it failed. A failure is logged with
    /// <paramref name="what"/>.
    /// </summary>
    private static Assembly? Resolve(Result<Assembly> loaded, string what)
    {
        if (loaded.Ok)
            return loaded.Value;

        Log.Error($"{what} could not be loaded: {loaded.Error}");

        return null;
    }

    /// <summary>
    /// Reads the assembly at <paramref name="path"/> into memory, with its symbols when they are beside it, or gives
    /// why not.
    /// </summary>
    /// <remarks>
    /// <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> locks the file on Windows while the gem runs, so a build
    /// could not write it and the run could not restart on the new code. The symbols keep files and lines in stack
    /// traces.
    /// </remarks>
    private Result<Assembly> LoadFromBytes(string path)
    {
        Result<byte[]> assembly = _files.ReadBytes(path);
        if (!assembly.Ok)
            return Result.Failure(assembly.Error);

        using MemoryStream image = new(assembly.Value);
        Result<byte[]> symbols = _files.ReadBytes(Path.ChangeExtension(path, ".pdb"));
        using MemoryStream? pdb = symbols.Ok ? new MemoryStream(symbols.Value) : null;

        // A build that is still writing the file leaves a half image.
        try
        {
            return Result.Success(LoadFromStream(image, pdb));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Finds the library's file under the gem's <c>runtimes/&lt;rid&gt;/native/</c>, with or without the Unix
    /// <c>lib</c> prefix; null when there is none.
    /// </summary>
    private string? FindCarried(string name)
    {
        string folder = _files.Combine(
            _files.GetParent(_path),
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
