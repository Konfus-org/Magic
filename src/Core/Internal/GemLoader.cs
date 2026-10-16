using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Internal;

/// <summary>
/// Loads the gems a project lists into the service provider <see cref="Engine"/> builds, and makes them once it has.
/// Each gem's assembly (<see cref="GemAssembly"/>) is registered as its class and as every interface it provides; one
/// that provides what Core or a gem listed before it provides is refused, as is a name listed twice. The types a gem's
/// assembly holds are taken in as it loads (<see cref="Code"/>), so files can name its components before any gem is
/// made, and where each assembly's assets are into <see cref="Project.Resources"/>. Magic's own scripts and the
/// project's own code, its scripts and components, are loaded first the same way, though neither is a gem, and loaded
/// again when they are built while the engine runs (<see cref="WatchCode"/>). The provider makes each gem after
/// whatever its constructor asks for, so <see cref="Make"/> returns the gems in dependency order. A constructor
/// parameter that is an <see cref="ISettings"/> is the gem's settings object, made by <see cref="Settings"/> as the
/// gem is. A gem that is an <see cref="ILogSink"/> is added to the log once it is made, instead of provided. Whatever
/// goes wrong with one gem is logged, and the others are made without it.
/// </summary>
internal sealed class GemLoader
{
    /// <summary>
    /// How long a code folder must have been left alone before what was built there is loaded: a build writes the
    /// assembly, its symbols and what it depends on one after another.
    /// </summary>
    private static readonly TimeSpan _settle = TimeSpan.FromMilliseconds(300);

    private readonly IFileSystem _files;
    private readonly List<IGem> _made = [];
    private readonly HashSet<Type> _tried = [];
    private readonly List<GemAssembly> _gems = [];

    /// <summary>
    /// The code that is no gem, as far as it was there to load, to load again when it is built.
    /// </summary>
    private readonly List<CodeFile> _code = [];

    /// <summary>
    /// Loads the project's code and the gems it lists, and registers the gems with <paramref name="services"/>, which
    /// already holds what Core provides.
    /// </summary>
    public GemLoader(ServiceCollection services, IFileSystem files, Project project)
    {
        _files = files;
        LoadCode(files.Combine(AppContext.BaseDirectory, "Resources", "Resources.dll"), "Magic's scripts");
        LoadCode(files.Combine(AppContext.BaseDirectory, "Projects", project.Name, project.Name + ".dll"), $"{project.Name}'s code");

        List<string> resources = [GemAssembly.ResourcesOf(files, typeof(GemLoader).Assembly)];
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in project.Gems)
        {
            if (!listed.Add(name))
            {
                Debugging.Error($"Gem {name} is listed twice in {project.Name}; it is made once.");
                continue;
            }

            Result<GemAssembly> loaded = GemAssembly.Load(files, name);
            if (!loaded.Ok)
            {
                Debugging.Error(loaded.Error);
                continue;
            }

            GemAssembly gem = loaded.Value;
            if (Clash(services, gem) is { } clash)
            {
                Debugging.Error(clash);
                continue;
            }

            services.AddSingleton(gem.Type, provider => MakeGem(provider, gem));
            foreach (Type contract in gem.Provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(gem.Type));
            }

            _gems.Add(gem);
            resources.Add(gem.Resources);
            Code.Add(gem.Path, gem.Types);
        }

        project.Resources = resources;
    }

    /// <summary>
    /// Watches where the code that is no gem was loaded from, until the subscription is disposed, and loads what is
    /// built there again once the build has settled: its types replace its last build's (<see cref="Code"/>), and
    /// <paramref name="assets"/> makes every script and every prefab again, as those are read as the classes code
    /// holds, so the world runs the scripts as they are now. The gems are never loaded again: a gem takes a restart.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The subscription returned owns the list, and disposing it disposes the list.")]
    public Subscription WatchCode(Assets assets)
    {
        SubscriptionList watching = new();
        foreach (CodeFile code in _code)
        {
            watching.Add(FileSystem.Watch(_files.Parent(code.Path), _ => ReloadCodeAsync(code, assets).FireAndForget()));
        }

        return new Subscription(watching.Dispose);
    }

    /// <summary>
    /// Makes every gem that was registered, in the order they are listed, each after whatever its constructor asks for;
    /// the gems, in the order they were made. A gem that could not be made has said why, and is left out.
    /// </summary>
    public List<IGem> Make(ServiceProvider container)
    {
        foreach (GemAssembly gem in _gems)
        {
            try
            {
                container.GetRequiredService(gem.Type);
            }
            catch (InvalidOperationException)
            {
                // MakeGem has logged why the gem was not made.
            }
        }

        return _made;
    }

    /// <summary>
    /// Loads the code at <paramref name="path"/> (<see cref="GemAssembly.LoadCode"/>) and takes in its types, and
    /// keeps it to load again when it is built.
    /// </summary>
    private void LoadCode(string path, string what)
    {
        if (GemAssembly.LoadCode(_files, path, what) is { } types)
        {
            Code.Add(path, types);
            _code.Add(new CodeFile(path, what));
        }
    }

    /// <summary>
    /// Something in <paramref name="code"/>'s folder changed: once it has settled, the last change to come loads the
    /// code again and has the scripts and prefabs made again.
    /// </summary>
    private async Task ReloadCodeAsync(CodeFile code, Assets assets)
    {
        int change = code.Note();
        await Task.Delay(_settle).ConfigureAwait(false);
        if (!code.IsLatest(change) || GemAssembly.ReloadCode(_files, code.Path, code.What) is not { } types)
        {
            return;
        }

        Code.Add(code.Path, types);
        assets.Touch<Script>();
        assets.Touch<Prefab>();
    }

    /// <summary>
    /// Why <paramref name="gem"/> is not made when it would provide something Core or a gem listed before it already
    /// provides; null when it provides nothing that is taken.
    /// </summary>
    private static string? Clash(ServiceCollection services, GemAssembly gem)
    {
        foreach (Type contract in gem.Provides)
        {
            foreach (ServiceDescriptor service in services)
            {
                // Core's services are instances, or made as their own class, and a gem is registered as its class before
                // the interfaces it provides, so what is found is the class that provides the interface.
                Type provider = service.ImplementationInstance?.GetType() ?? service.ServiceType;
                if (provider.IsAssignableTo(contract))
                {
                    return $"Gem {gem.Name} provides {NameOf(contract)}, which {provider.Name} already provides; it is not made.";
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
    /// asks for, which the provider makes first, adds it to what was made, and adds it to the log if it is an
    /// <see cref="ILogSink"/>. Why it could not be made, settings that do not fit it among the reasons, is logged here,
    /// against the gem, and the gems that need it are told only that it was not. A gem is tried once, which also ends
    /// a cycle of gems that need each other.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on.")]
    private IGem MakeGem(IServiceProvider services, GemAssembly gem)
    {
        if (!_tried.Add(gem.Type))
        {
            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");
        }

        Result<ISettings?> settings = services.GetRequiredService<Settings>().Make(gem.Name, gem.Settings);
        if (!settings.Ok)
        {
            Debugging.Error($"Gem {gem.Name} was not made: {settings.Error}");
            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");
        }

        IGem made;
        try
        {
            made = (IGem)ActivatorUtilities.CreateInstance(services, gem.Type, settings.Value is { } given ? [given] : []);
        }
        catch (Exception ex)
        {
            // A service that is missing or was not made, and a gem that cannot start, say why in the message; anything
            // else is a bug in the gem, and its stack says where.
            Debugging.Error($"Gem {gem.Name} was not made: {(ex is InvalidOperationException ? ex.Message : ex.ToString())}");
            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.", ex);
        }

        _made.Add(made);
        if (made is ILogSink sink)
        {
            Debugging.Add(sink);
        }

        Debugging.Info($"Gem {gem.Name} started");
        return made;
    }

    /// <summary>
    /// Code that is no gem: where it is built, what to call it in the log, and the changes its folder has had, of
    /// which the latest loads it once it has settled.
    /// </summary>
    private sealed class CodeFile(string path, string what)
    {
        private int _changes;

        public string Path { get; } = path;

        public string What { get; } = what;

        /// <summary>
        /// Notes one more change to its folder: which one, to ask <see cref="IsLatest"/> of after the settle.
        /// </summary>
        public int Note()
        {
            return Interlocked.Increment(ref _changes);
        }

        public bool IsLatest(int change)
        {
            return change == Volatile.Read(ref _changes);
        }
    }
}
