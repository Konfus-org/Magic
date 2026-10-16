using System.IO;
using System.Reflection;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// What a gem's assembly holds, read from its dll: gem <c>Name</c> is Gems/Name/Name.dll beside Magic, loaded into a
/// context of its own (<see cref="GemLoadContext"/>). Nothing is made here; this is what making the gem takes.
/// </summary>
/// <param name="Name">The gem's name, as the project lists it.</param>
/// <param name="Path">Where its assembly was loaded from.</param>
/// <param name="Type">Its one <see cref="IGem"/> class.</param>
/// <param name="Settings">The settings type its constructor takes, if any.</param>
/// <param name="Provides">What it provides: every interface its class implements but <see cref="IGem"/>,
/// <see cref="IDisposable"/> and <see cref="ILogSink"/>, which Core takes itself, so any number of gems can be sinks.</param>
/// <param name="Types">Every type in its assembly, among them the components it declares.</param>
/// <param name="Resources">The folder of its own assets (<see cref="ResourcesOf"/>).</param>
internal readonly record struct GemAssembly(string Name, string Path, Type Type, Type? Settings, List<Type> Provides, Type[] Types, string Resources)
{
    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    /// <summary>
    /// The types of every assembly loaded so far, by its path. A context is never unloaded, so each loads once for the
    /// process: a run started again (<see cref="Lifetime.Restart"/>) finds the types it had, not a second copy of each.
    /// </summary>
    private static readonly Dictionary<string, Type[]> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _loading = new();

    /// <summary>
    /// Reads gem <paramref name="name"/>'s assembly: it must hold exactly one <see cref="IGem"/> class, with one public
    /// constructor that takes at most one <see cref="ISettings"/>. Why not, when it does not, or cannot be loaded.
    /// </summary>
    public static Result<GemAssembly> Load(IFileSystem files, string name)
    {
        string path = files.Combine(AppContext.BaseDirectory, "Gems", name, name + ".dll");
        Result<Type[]> loaded = LoadTypes(files, path, $"Gem {name}", isCode: false, again: false);
        if (!loaded.Ok)
        {
            return Result.Failure<GemAssembly>(loaded.Error);
        }

        Type[] types = loaded.Value;
        List<Type> gems = [];
        foreach (Type type in types)
        {
            if (type.IsClass && !type.IsAbstract && type.IsAssignableTo(typeof(IGem)))
            {
                gems.Add(type);
            }
        }

        if (gems.Count != 1)
        {
            return Result.Failure<GemAssembly>($"Gem {name} must hold exactly one class that implements IGem; it holds {gems.Count}.");
        }

        Type gem = gems[0];
        ConstructorInfo[] constructors = gem.GetConstructors();
        if (constructors.Length != 1)
        {
            return Result.Failure<GemAssembly>($"Gem {name}: {gem.Name} must have exactly one public constructor; it has {constructors.Length}.");
        }

        Type? settings = null;
        foreach (ParameterInfo parameter in constructors[0].GetParameters())
        {
            Type type = parameter.ParameterType;
            if (!type.IsAssignableTo(typeof(ISettings)))
            {
                continue;
            }

            if (settings is not null)
            {
                return Result.Failure<GemAssembly>($"Gem {name}: {gem.Name} takes two settings objects, {settings.Name} and {type.Name}; a gem has one.");
            }

            settings = type;
        }

        List<Type> provides = [];
        foreach (Type contract in gem.GetInterfaces())
        {
            if (contract != typeof(IGem) && contract != typeof(IDisposable) && contract != typeof(ILogSink))
            {
                provides.Add(contract);
            }
        }

        return Result.Success(new GemAssembly(name, path, gem, settings, provides, types, ResourcesOf(files, gem.Assembly)));
    }

    /// <summary>
    /// The types of code that is no gem, scripts and the components they use, built as the assembly at
    /// <paramref name="path"/>: the project's own, the <c>.cs</c> files under its <c>Assets/</c>, which the csproj
    /// beside its <c>.magic</c> file compiles into Projects/Name/Name.dll beside Magic, Name being the project's; and
    /// Magic's, the <c>.cs</c> files among the bundled resources, compiled into Resources/Resources.dll beside it. Each
    /// loads into a context of its own, as a gem does, though it provides nothing and takes no settings. Null when
    /// <paramref name="what"/> is not built, as a project needs no code, and when it cannot be loaded, which is logged.
    /// </summary>
    public static Type[]? LoadCode(IFileSystem files, string path, string what)
    {
        return files.FileExists(path) ? LoadCodeTypes(files, path, what, again: false) : null;
    }

    /// <summary>
    /// Loads code (<see cref="LoadCode"/>) again, as it is built now, into a new context beside the last, which stays
    /// for whatever holds its types: the types the file holds now, or null when it cannot be loaded, which is logged.
    /// </summary>
    public static Type[]? ReloadCode(IFileSystem files, string path, string what)
    {
        return LoadCodeTypes(files, path, what, again: true);
    }

    private static Type[]? LoadCodeTypes(IFileSystem files, string path, string what, bool again)
    {
        Result<Type[]> loaded = LoadTypes(files, path, what, isCode: true, again);
        if (!loaded.Ok)
        {
            Debugging.Error(loaded.Error);
            return null;
        }

        Debugging.Info(again ? $"Loaded {what} again" : $"Loaded {what}");
        return loaded.Value;
    }

    /// <summary>
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded as
    /// <see cref="ResourcesKey"/>, its source <c>Resources/</c>, which every build but a release records; else
    /// <c>Resources/</c> beside it, where a release copies them.
    /// </summary>
    public static string ResourcesOf(IFileSystem files, Assembly assembly)
    {
        foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, ResourcesKey, StringComparison.Ordinal) && metadata.Value is { Length: > 0 } folder)
            {
                return folder;
            }
        }

        return files.Combine(files.Parent(assembly.Location), "Resources");
    }

    /// <summary>
    /// Every type in the assembly at <paramref name="path"/>, loaded into a context of its own the first time it is
    /// asked for, or once more when asked <paramref name="again"/>, beside the context before. Code
    /// (<paramref name="isCode"/>) is read into memory and loaded from there, so a build can write the file while it is
    /// loaded; a gem is loaded from its file, so its assembly knows where it is, which its native libraries are found
    /// by. Why not when there is no such file or it is not one .NET can load, naming <paramref name="what"/> it is.
    /// </summary>
    private static Result<Type[]> LoadTypes(IFileSystem files, string path, string what, bool isCode, bool again)
    {
        lock (_loading)
        {
            if (!again && _loaded.TryGetValue(path, out Type[]? loaded))
            {
                return Result.Success(loaded);
            }

            if (!files.FileExists(path))
            {
                return Result.Failure<Type[]>($"{what} was not found: there is no {path}.");
            }

            try
            {
                GemLoadContext context = new(path);
                Type[] types = (isCode ? LoadFromBytes(files, context, path) : context.LoadFromAssemblyPath(path)).GetTypes();
                _loaded[path] = types;
                return Result.Success(types);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or IOException)
            {
                return Result.Failure<Type[]>($"{what} could not be loaded from {path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The assembly at <paramref name="path"/> loaded into <paramref name="context"/> from its bytes, with its symbols
    /// from the <c>.pdb</c> beside it when there is one, so what its code logs names its lines.
    /// </summary>
    private static Assembly LoadFromBytes(IFileSystem files, GemLoadContext context, string path)
    {
        Result<byte[]> read = files.Read(path);
        if (!read.Ok)
        {
            throw new IOException(read.Error);
        }

        using MemoryStream assembly = new(read.Value);
        string symbolsPath = System.IO.Path.ChangeExtension(path, ".pdb");
        if (!files.FileExists(symbolsPath) || files.Read(symbolsPath) is not { Ok: true } symbols)
        {
            return context.LoadFromStream(assembly);
        }

        using MemoryStream pdb = new(symbols.Value);
        return context.LoadFromStream(assembly, pdb);
    }
}
