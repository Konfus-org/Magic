using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Host;

/// <summary>
/// Loads the gems a project lists and makes them, each after the gems its constructor asks for. One instance does one
/// load: <see cref="Register"/>, build the provider, then <see cref="Create"/>. A gem that can't be loaded or made is
/// logged, and the others go on without it.
/// </summary>
internal sealed class GemLoader
{
    /// <summary>
    /// The gems made, in the order their constructors finished, which is dependency order.
    /// </summary>
    private readonly List<CreatedGem> _created = [];

    /// <summary>
    /// The gems whose constructors have run or are running, so a failed one isn't made twice.
    /// </summary>
    private readonly HashSet<Type> _tried = [];

    /// <summary>
    /// The gems whose constructors are running, so one asked for again is reported as a cycle; the provider would hang.
    /// </summary>
    private readonly HashSet<Type> _creating = [];

    /// <summary>
    /// Loads the gems <paramref name="project"/> lists and registers each with <paramref name="services"/> as its class
    /// and every interface it provides. A name listed twice, the wrong version, or an interface already provided is
    /// refused.
    /// </summary>
    internal List<LoadedGem> Register(ServiceCollection services, IFileSystem files, Project project)
    {
        List<LoadedGem> registered = [];
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in project.Gems)
        {
            // A gem is listed by its name, then @ and the version it must be where that matters.
            int at = listing.IndexOf('@', StringComparison.Ordinal);
            string name = at < 0 ? listing : listing[..at];
            string wanted = at < 0 ? string.Empty : listing[(at + 1)..];
            if (!listed.Add(name))
            {
                Log.Error($"Gem {name} is listed twice in {project.Name}; it is made once.");
                continue;
            }

            Result<LoadedGem> loaded = Load(files, name);
            if (!loaded.Ok)
            {
                Log.Error(loaded.Error);
                continue;
            }

            LoadedGem gem = loaded.Value;
            if (wanted.Length > 0 && !IsVersion(gem.Version, wanted))
            {
                Log.Error($"Gem {name} is {gem.Version.ToString(3)}, but {project.Name} asks for {wanted}.");
                continue;
            }

            if (FindClash(services, gem) is { } clash)
            {
                Log.Error(clash);
                continue;
            }

            services.AddSingleton(gem.Type, provider => CreateGem(provider, gem));
            foreach (Type contract in gem.Provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(gem.Type));
            }

            registered.Add(gem);
        }

        return registered;
    }

    /// <summary>
    /// Makes each of <paramref name="gems"/> from <paramref name="provider"/>; the gems made, in dependency order.
    /// </summary>
    internal List<CreatedGem> Create(ServiceProvider provider, List<LoadedGem> gems)
    {
        foreach (LoadedGem gem in gems)
        {
            try
            {
                provider.GetRequiredService(gem.Type);
            }
            catch (InvalidOperationException)
            {
                // CreateGem has logged why.
            }
        }

        return _created;
    }

    /// <summary>
    /// The gem <paramref name="name"/> from <c>Gems/&lt;name&gt;/&lt;name&gt;.dll</c> beside Magic, or why it can't be
    /// made: it must hold one <see cref="IGem"/> class with one public constructor.
    /// </summary>
    private static Result<LoadedGem> Load(IFileSystem files, string name)
    {
        string path = files.Combine(AppContext.BaseDirectory, "Gems", name, name + ".dll");
        Result<Assembly> loaded = LoadAssembly(files, path, name);
        if (!loaded.Ok)
            return Result.Failure(loaded.Error);

        Assembly assembly = loaded.Value;
        List<Type> gems = [];
        foreach (Type type in GetTypes(assembly))
        {
            if (type.IsClass && !type.IsAbstract && type.IsAssignableTo(typeof(IGem)))
                gems.Add(type);
        }

        if (gems.Count != 1)
            return Result.Failure($"Gem {name} must hold one class that implements IGem; it holds {gems.Count}.");

        Type gem = gems[0];
        int constructors = gem.GetConstructors().Length;
        if (constructors != 1)
            return Result.Failure($"Gem {name}: {gem.Name} must have one public constructor; it has {constructors}.");

        // What the host takes itself isn't provided, so any number of gems can be sinks.
        List<Type> provides = [];
        foreach (Type contract in gem.GetInterfaces())
        {
            if (contract != typeof(IGem) && contract != typeof(IDisposable) && contract != typeof(ILogSink))
                provides.Add(contract);
        }

        string author = GetMetadata(assembly, "Magic.Author") ?? name;
        string description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;
        Version version = assembly.GetName().Version ?? new Version(1, 0, 0);

        return Result.Success(new LoadedGem(name, path, author, description, version, gem, provides, assembly));
    }

    /// <summary>
    /// The assembly at <paramref name="path"/>: the one already loaded from there, since a gem is never unloaded, else
    /// loaded from its file into a <see cref="GemLoadContext"/> of its own.
    /// </summary>
    private static Result<Assembly> LoadAssembly(IFileSystem files, string path, string name)
    {
        foreach (AssemblyLoadContext context in AssemblyLoadContext.All)
        {
            if (context is not GemLoadContext gem || !string.Equals(gem.GemPath, path, StringComparison.Ordinal))
                continue;

            foreach (Assembly assembly in gem.Assemblies)
            {
                if (string.Equals(assembly.Location, path, StringComparison.Ordinal))
                    return Result.Success(assembly);
            }
        }

        if (!files.FileExists(path))
            return Result.Failure($"Gem {name} was not found: there is no {path}.");

        try
        {
            return Result.Success(new GemLoadContext(path, files).LoadFromAssemblyPath(path));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            return Result.Failure($"Gem {name} could not be loaded from {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Every type <paramref name="assembly"/> holds; those that load, when some don't.
    /// </summary>
    private static IReadOnlyList<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            List<Type> loaded = [];
            foreach (Type? type in ex.Types)
            {
                if (type is not null)
                    loaded.Add(type);
            }

            return loaded;
        }
    }

    /// <summary>
    /// What <paramref name="assembly"/>'s build recorded as <paramref name="key"/>; null when nothing, or an empty
    /// string, was.
    /// </summary>
    private static string? GetMetadata(Assembly assembly, string key)
    {
        foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, key, StringComparison.Ordinal) && metadata.Value is { Length: > 0 } value)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="version"/> matches each part <paramref name="wanted"/> gives (<c>1</c>, <c>1.2</c>,
    /// <c>1.2.3</c>); anything but one to four numbers matches nothing.
    /// </summary>
    private static bool IsVersion(Version version, string wanted)
    {
        ReadOnlySpan<int> parts = [version.Major, version.Minor, version.Build, version.Revision];
        string[] asked = wanted.Split('.');
        if (asked.Length > parts.Length)
            return false;

        for (int i = 0; i < asked.Length; i++)
        {
            bool isNumber = int.TryParse(asked[i], NumberStyles.None, CultureInfo.InvariantCulture, out int part);
            if (!isNumber || part != parts[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Why <paramref name="gem"/> is refused when <paramref name="services"/> already provides an interface it does;
    /// null when none.
    /// </summary>
    private static string? FindClash(ServiceCollection services, LoadedGem gem)
    {
        foreach (Type contract in gem.Provides)
        {
            foreach (ServiceDescriptor service in services)
            {
                // A gem is registered as its class before its interfaces, so the class is what is found.
                Type provider = service.ImplementationInstance?.GetType() ?? service.ServiceType;
                if (provider.IsAssignableTo(contract))
                    return $"Gem {gem.Name} provides {GetName(contract)}, which {provider.Name} already provides.";
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s name as C# writes it: <c>IAssetLoader&lt;Texture&gt;</c>, not <c>IAssetLoader`1</c>.
    /// </summary>
    private static string GetName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];

        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), GetName))}>";
    }

    /// <summary>
    /// The provider's factory for <paramref name="gem"/>. Why it couldn't be made is logged here, once; the gems that
    /// need it are told only that it wasn't.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    private IGem CreateGem(IServiceProvider services, LoadedGem gem)
    {
        if (_creating.Contains(gem.Type))
            throw new InvalidOperationException($"it needs {gem.Name}, which needs it: a cycle.");

        if (!_tried.Add(gem.Type))
            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");

        IGem made;
        _creating.Add(gem.Type);
        try
        {
            made = (IGem)ActivatorUtilities.CreateInstance(services, gem.Type);
        }
        catch (Exception ex)
        {
            // A missing service or a gem that can't start says why in the message; anything else is a bug in the gem,
            // and its stack says where.
            Log.Error($"Gem {gem.Name} was not made: {(ex is InvalidOperationException ? ex.Message : ex.ToString())}");

            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.", ex);
        }
        finally
        {
            _creating.Remove(gem.Type);
        }

        Subscription? sink = made is ILogSink logSink ? Log.Register(logSink) : null;
        _created.Add(new CreatedGem(made, gem.Name, sink));

        string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
        Log.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");

        return made;
    }

    /// <summary>
    /// A gem as made, the name the project lists it by, and its place on the log when it is a sink, to dispose before
    /// the gem.
    /// </summary>
    internal readonly record struct CreatedGem(IGem Gem, string Name, Subscription? LogSink);
}
