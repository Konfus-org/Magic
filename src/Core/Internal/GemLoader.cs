using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Internal;

/// <summary>
/// Turns the names in a project's gem list into running gems. Gem <c>Name</c> is the one <see cref="IGem"/> class in
/// Gems/Name/Name.dll beside Magic. Core's services, <see cref="Assets"/> and <see cref="Caches"/> among them, every gem and the interfaces each
/// provides, and the <see cref="World"/> over the ECS a gem provides, are registered with a service provider, which makes the gems in the order they are listed, each after
/// whatever its constructor asks for, so the gems come back in dependency order. A constructor parameter whose type the gem
/// declares itself is its settings object, made by <see cref="Settings"/> as the gem is, from the presets it read before
/// any gem was made. A gem that is an <see cref="ILogSink"/>
/// is added to the log once it is made, instead of provided. The components a gem declares are taken in as its
/// assembly loads, so files can name them (<see cref="Components.Get"/>) before any gem is made, and where each
/// assembly's assets are into <see cref="Project.Resources"/>. The project's own code, its scripts and components, is
/// loaded first the same way, though it is no gem. Whatever goes wrong with one gem is logged, and the others are made
/// without it.
/// </summary>
internal static class GemLoader
{
    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    /// <summary>
    /// Loads and makes the project's gems, in the order their hooks run, each with its settings object; the settings
    /// they were made from; and the world, over the ECS a gem provides; none, which is warned about, when no gem does.
    /// </summary>
    public static (List<IGem> Gems, World? World, Settings Settings) Load(Project project, IFileSystem files, Lifetime lifetime, Threads threads, Events events, Args args)
    {
        // What a gem's constructor can ask for: Core's services, then what the gems provide. The assets, the settings
        // and the cache are made once every gem's assembly, and so where its assets are, has been loaded.
        ServiceCollection services = new();
        services.AddSingleton(files);
        services.AddSingleton(lifetime);
        services.AddSingleton(threads);
        services.AddSingleton(events);
        services.AddSingleton(project);
        services.AddSingleton(args);
        services.AddSingleton(provider => new Assets(files, project, lifetime, events, provider));
        services.AddSingleton(provider => new Settings(provider.GetRequiredService<Assets>(), events, project, args));
        services.AddSingleton(provider => new Caches(files, provider));

        if (LoadProjectCode(files, project) is { } code)
        {
            Components.Add(code);
        }

        List<IGem> made = [];
        HashSet<Type> tried = [];
        List<Candidate> registered = [];
        List<string> resources = [ResourcesOf(files, typeof(GemLoader).Assembly)];
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in project.Gems)
        {
            if (!listed.Add(name))
            {
                Debugging.Error($"Gem {name} is listed twice in {project.Name}; it is made once.");
                continue;
            }

            Result<Candidate> loaded = LoadAssembly(files, name);
            if (!loaded.Ok)
            {
                Debugging.Error(loaded.Error);
                continue;
            }

            Candidate candidate = loaded.Value;
            List<Type> provides = Provides(candidate.Type);
            if (Clash(services, name, provides) is { } clash)
            {
                Debugging.Error(clash);
                continue;
            }

            services.AddSingleton(candidate.Type, provider => Make(provider, candidate, made, tried));
            foreach (Type contract in provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(candidate.Type));
            }

            registered.Add(candidate);
            resources.Add(ResourcesOf(files, candidate.Type.Assembly));
            Components.Add(candidate.Types);
        }

        project.Resources = resources;

        // The world is made once a gem asks for it, or once every gem is, over the ECS and the scripts the gems provide.
        bool hasEcs = false;
        foreach (ServiceDescriptor service in services)
        {
            hasEcs |= service.ServiceType == typeof(IEcs);
        }

        if (hasEcs)
        {
            services.AddSingleton(provider => new World(provider.GetRequiredService<IEcs>(), provider.GetService<IScripts>(), provider.GetRequiredService<Assets>(), threads, events, project, lifetime));
        }

        // Not disposed: Engine ends the gems itself, one at a time, so one that throws cannot stop the rest ending. The
        // settings are made first, as every gem's are made from the presets they read.
        ServiceProvider container = services.BuildServiceProvider();
        Settings settings = container.GetRequiredService<Settings>();
        foreach (Candidate candidate in registered)
        {
            try
            {
                container.GetRequiredService(candidate.Type);
            }
            catch (InvalidOperationException)
            {
                // Make has logged why the gem was not made.
            }
        }

        return (made, WorldOf(container, hasEcs), settings);
    }

    /// <summary>
    /// The world, when a gem provides the ECS and it was made; null otherwise, which is said, as no domain then opens.
    /// </summary>
    private static World? WorldOf(ServiceProvider container, bool hasEcs)
    {
        if (!hasEcs)
        {
            Debugging.Warn("No gem the project lists provides the ECS, so there is no world, and no domain opens.");
            return null;
        }

        try
        {
            return container.GetRequiredService<World>();
        }
        catch (InvalidOperationException ex)
        {
            Debugging.Error($"There is no world, and no domain opens: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded as
    /// <see cref="ResourcesKey"/>, its source <c>Resources/</c>, which every build but a release records; else
    /// <c>Resources/</c> beside it, where a release copies them.
    /// </summary>
    private static string ResourcesOf(IFileSystem files, Assembly assembly)
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
    /// The types of the project's own code, its scripts and the components they use: the <c>.cs</c> files under its
    /// <c>Assets/</c>, which the project's csproj compiles into Projects/Name/Name.dll beside Magic, Name being the
    /// project's. It loads into a context of its own, as a gem does, though it is no gem: it provides nothing, and takes
    /// no settings. Null when the project has none built, as a project needs none, and when it cannot be loaded, which
    /// is logged.
    /// </summary>
    private static Type[]? LoadProjectCode(IFileSystem files, Project project)
    {
        string path = files.Combine(AppContext.BaseDirectory, "Projects", project.Name, project.Name + ".dll");
        if (!files.FileExists(path))
        {
            return null;
        }

        try
        {
            Type[] types = new GemLoadContext(path).LoadFromAssemblyPath(path).GetTypes();
            Debugging.Info($"Loaded {project.Name}'s code");
            return types;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
        {
            Debugging.Error($"{project.Name}'s code could not be loaded from {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads gem <paramref name="name"/>'s assembly and finds what making it takes: its one <see cref="IGem"/> class,
    /// and its settings type if its constructor takes settings.
    /// </summary>
    private static Result<Candidate> LoadAssembly(IFileSystem files, string name)
    {
        string path = files.Combine(AppContext.BaseDirectory, "Gems", name, name + ".dll");
        if (!files.FileExists(path))
        {
            return Result.Failure<Candidate>($"Gem {name} was not found: there is no {path}.");
        }

        Type[] types;
        try
        {
            types = new GemLoadContext(path).LoadFromAssemblyPath(path).GetTypes();
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
        {
            return Result.Failure<Candidate>($"Gem {name} could not be loaded from {path}: {ex.Message}");
        }

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
            return Result.Failure<Candidate>($"Gem {name} must hold exactly one class that implements IGem; it holds {gems.Count}.");
        }

        Type gem = gems[0];
        ConstructorInfo[] constructors = gem.GetConstructors();
        if (constructors.Length != 1)
        {
            return Result.Failure<Candidate>($"Gem {name}: {gem.Name} must have exactly one public constructor; it has {constructors.Length}.");
        }

        Type? settings = null;
        foreach (ParameterInfo parameter in constructors[0].GetParameters())
        {
            Type type = parameter.ParameterType;
            if (type.IsInterface || type.Assembly != gem.Assembly)
            {
                continue;
            }

            if (settings is not null)
            {
                return Result.Failure<Candidate>($"Gem {name}: {gem.Name} takes two settings objects, {settings.Name} and {type.Name}; a gem has one.");
            }

            settings = type;
        }

        return Result.Success(new Candidate(name, gem, settings, types));
    }

    /// <summary>
    /// What a gem provides: every interface its class implements but <see cref="IGem"/>, <see cref="IDisposable"/> and
    /// <see cref="ILogSink"/>, which Core takes itself, so any number of gems can be sinks.
    /// </summary>
    private static List<Type> Provides(Type gem)
    {
        List<Type> provides = [];
        foreach (Type contract in gem.GetInterfaces())
        {
            if (contract != typeof(IGem) && contract != typeof(IDisposable) && contract != typeof(ILogSink))
            {
                provides.Add(contract);
            }
        }

        return provides;
    }

    /// <summary>
    /// Why gem <paramref name="name"/> is not made when it would provide something Core or a gem listed before it
    /// already provides; null when it provides nothing that is taken.
    /// </summary>
    private static string? Clash(ServiceCollection services, string name, List<Type> provides)
    {
        foreach (Type contract in provides)
        {
            foreach (ServiceDescriptor service in services)
            {
                // Core's services are instances, or made as their own class, and a gem is registered as its class before
                // the interfaces it provides, so what is found is the class that provides the interface.
                Type provider = service.ImplementationInstance?.GetType() ?? service.ServiceType;
                if (provider.IsAssignableTo(contract))
                {
                    return $"Gem {name} provides {NameOf(contract)}, which {provider.Name} already provides; it is not made.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s name as C# writes it, <c>IAssetLoader&lt;Texture&gt;</c> rather than <c>IAssetLoader`1</c>.
    /// </summary>
    private static string NameOf(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), NameOf))}>";
    }

    /// <summary>
    /// The service provider's factory for a gem: makes it from its settings object and the services its constructor
    /// asks for, which the provider makes first, adds it to <paramref name="made"/>, and adds it to the log if it is an
    /// <see cref="ILogSink"/>. Why it could not be made, settings that do not fit it among the reasons, is logged here,
    /// against the gem, and the gems that need it are told only that it was not. A gem is tried once
    /// (<paramref name="tried"/>), which also ends a cycle of gems that need each other.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on.")]
    private static IGem Make(IServiceProvider services, Candidate candidate, List<IGem> made, HashSet<Type> tried)
    {
        if (!tried.Add(candidate.Type))
        {
            throw new InvalidOperationException($"it needs {candidate.Name}, which was not made.");
        }

        Result<object?> settings = services.GetRequiredService<Settings>().Make(candidate.Name, candidate.Settings);
        if (!settings.Ok)
        {
            Debugging.Error($"Gem {candidate.Name} was not made: {settings.Error}");
            throw new InvalidOperationException($"it needs {candidate.Name}, which was not made.");
        }

        IGem gem;
        try
        {
            gem = (IGem)ActivatorUtilities.CreateInstance(services, candidate.Type, settings.Value is { } given ? [given] : []);
        }
        catch (Exception ex)
        {
            // A service that is missing or was not made, and a gem that cannot start, say why in the message; anything
            // else is a bug in the gem, and its stack says where.
            Debugging.Error($"Gem {candidate.Name} was not made: {(ex is InvalidOperationException ? ex.Message : ex.ToString())}");
            throw new InvalidOperationException($"it needs {candidate.Name}, which was not made.", ex);
        }

        made.Add(gem);
        if (gem is ILogSink sink)
        {
            Debugging.Add(sink);
        }

        Debugging.Info($"Gem {candidate.Name} started");
        return gem;
    }

    /// <param name="Name">The gem's name, as the project lists it.</param>
    /// <param name="Type">Its one <see cref="IGem"/> class.</param>
    /// <param name="Settings">The settings type its constructor takes, if any.</param>
    /// <param name="Types">Every type in its assembly, among them the components it declares.</param>
    private readonly record struct Candidate(string Name, Type Type, Type? Settings, Type[] Types);
}
