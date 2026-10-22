using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
/// that provides what Core or a gem listed before it provides is refused, as is a name listed twice, and one that is
/// not the version the project asks for (<see cref="Project.Gems"/>). The types a gem's
/// assembly is taken in as it loads (<see cref="Assemblies"/>), so files can name its components and scripts before
/// any gem is made, and the folder of its own assets is added to the assets (<see cref="Assets.AddRoot"/>), which read
/// it while the next gem loads. Magic's own scripts and the project's own code, its scripts and components, are loaded
/// first the same way, though neither is a gem, and loaded again when they are built while the engine runs, until the
/// loader is disposed. The provider makes each gem after whatever its constructor asks for, so <see cref="Load"/>
/// returns the gems in dependency order. A constructor parameter that is an <see cref="ISettings"/> is the gem's
/// settings object, made by <see cref="Settings"/> as the gem is. A gem that is an <see cref="ILogSink"/> is added to
/// the log once it is made, instead of provided, and one that is an <see cref="IPreloader"/> is given to the world by
/// whoever made the gems. Whatever goes wrong with one gem is logged, and the others are made without it.
/// </summary>
internal sealed class GemLoader : IDisposable
{
    /// <summary>
    /// How long a code folder must have been left alone before what was built there is loaded: a build writes the
    /// assembly, its symbols and what it depends on one after another.
    /// </summary>
    private static readonly TimeSpan _settle = TimeSpan.FromMilliseconds(300);

    private readonly IFileSystem _files;
    private readonly Assemblies _assemblies;
    private readonly Assets _assets;
    private readonly List<IGem> _made = [];
    private readonly HashSet<Type> _tried = [];

    /// <summary>
    /// The gems whose constructors are running, so one asked for by what it needs is told it is a cycle.
    /// </summary>
    private readonly HashSet<Type> _making = [];
    private readonly List<GemAssembly> _gems = [];

    /// <summary>
    /// The code that is no gem, as far as it was there to load, to load again when it is built.
    /// </summary>
    private readonly List<CodeFile> _code = [];

    /// <summary>
    /// The watches on the folders <see cref="_code"/> is built in, until the loader is disposed.
    /// </summary>
    private readonly SubscriptionList _codeWatch = new();

    /// <summary>
    /// Loads the project's code and the gems it lists, registers the gems with <paramref name="services"/>, which
    /// already holds what Core provides, and adds each one's own assets to <paramref name="assets"/> as it loads it.
    /// </summary>
    internal GemLoader(
        ServiceCollection services,
        IFileSystem files,
        Project project,
        Assemblies assemblies,
        Assets assets)
    {
        _files = files;
        _assemblies = assemblies;
        _assets = assets;

        LoadCode(files.Combine(AppContext.BaseDirectory, "Resources", "Resources.dll"), "Magic's scripts");
        LoadCode(
            files.Combine(AppContext.BaseDirectory, "Projects", project.Name, project.Name + ".dll"),
            $"{project.Name}'s code");

        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in project.Gems)
        {
            int at = listing.IndexOf('@', StringComparison.Ordinal);
            string name = at < 0 ? listing : listing[..at];
            string wanted = at < 0 ? string.Empty : listing[(at + 1)..];
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
            if (wanted.Length > 0 && !IsVersion(gem.Version, wanted))
            {
                Debugging.Error(
                    $"Gem {name} is {gem.Version.ToString(3)}, and {project.Name} asks for {wanted}; it is not made.");

                continue;
            }

            if (FindClash(services, gem) is { } clash)
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
            assets.AddRoot(gem.Resources);
            _assemblies.Add(gem.Path, gem.Assembly);
        }

        foreach (CodeFile code in _code)
        {
            _codeWatch.Add(FileSystem.Watch(_files.GetParent(code.Path), _ => ReloadCodeAsync(code).FireAndForget()));
        }
    }

    public void Dispose() => _codeWatch.Dispose();

    /// <summary>
    /// Makes every gem that was registered, in the order they are listed, each after whatever its constructor asks for;
    /// the gems, in the order they were made. A gem that could not be made has said why, and is left out.
    /// </summary>
    internal List<IGem> Load(ServiceProvider container)
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
    /// Whether <paramref name="version"/> is the one a project asks for as <paramref name="wanted"/>: the parts it
    /// gives, <c>1</c>, <c>1.2</c> or <c>1.2.3</c>, each match, and the rest may be anything. Anything that is not
    /// one to four numbers matches no version.
    /// </summary>
    private static bool IsVersion(Version version, string wanted)
    {
        ReadOnlySpan<int> parts = [version.Major, version.Minor, version.Build, version.Revision];
        string[] asked = wanted.Split('.');

        if (asked.Length > parts.Length) return false;

        for (int i = 0; i < asked.Length; i++)
        {
            if (!int.TryParse(asked[i], NumberStyles.None, CultureInfo.InvariantCulture, out int part)
                || part != parts[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Why <paramref name="gem"/> is not made when it would provide something Core or a gem listed before it already
    /// provides; null when it provides nothing that is taken.
    /// </summary>
    private static string? FindClash(ServiceCollection services, GemAssembly gem)
    {
        foreach (Type contract in gem.Provides)
        {
            foreach (ServiceDescriptor service in services)
            {
                // Core's services are instances, or made as their own class, and a gem is registered as its class
                // before the interfaces it provides, so what is found is the class that provides the interface.
                Type provider = service.ImplementationInstance?.GetType() ?? service.ServiceType;

                if (provider.IsAssignableTo(contract))
                {
                    return $"Gem {gem.Name} provides {GetName(contract)}, "
                        + $"which {provider.Name} already provides; it is not made.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s name as C# writes it, <c>IAssetLoader&lt;Texture&gt;</c> rather than
    /// <c>IAssetLoader`1</c>.
    /// </summary>
    private static string GetName(Type type)
    {
        if (!type.IsGenericType) return type.Name;

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];

        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), GetName))}>";
    }

    /// <summary>
    /// Loads the code at <paramref name="path"/> (<see cref="GemAssembly.LoadCode"/>) and takes in its assembly, and
    /// keeps it to load again when it is built.
    /// </summary>
    private void LoadCode(string path, string what)
    {
        if (GemAssembly.LoadCode(_files, path, what, again: false) is not { } assembly) return;

        _assemblies.Add(path, assembly);
        _code.Add(new CodeFile(path, what));
    }

    /// <summary>
    /// Something in <paramref name="code"/>'s folder changed: once it has settled, the last change to come loads the
    /// code again and has the scripts made again, then the prefabs, so each names the scripts' classes as they are now.
    /// </summary>
    private async Task ReloadCodeAsync(CodeFile code)
    {
        int change = code.Note();
        await Task.Delay(_settle).ConfigureAwait(false);

        if (!code.IsLatest(change)
            || GemAssembly.LoadCode(_files, code.Path, code.What, again: true) is not { } assembly)
        {
            return;
        }

        _assemblies.Add(code.Path, assembly);
        await _assets.RemakeAsync<Script>().ConfigureAwait(false);
        await _assets.RemakeAsync<Prefab>().ConfigureAwait(false);
    }

    /// <summary>
    /// The service provider's factory for a gem: makes it from its settings object and the services its constructor
    /// asks for, which the provider makes first, adds it to what was made, and adds it to the log if it is an
    /// <see cref="ILogSink"/>. Why it could not be made, settings that do not fit it among the reasons, is logged here,
    /// against the gem, and the gems that need it are told only that it was not. A gem is tried once; one asked for
    /// while its constructor runs, by something it needs, is a cycle, which is said.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on.")]
    private IGem MakeGem(IServiceProvider services, GemAssembly gem)
    {
        if (_making.Contains(gem.Type))
        {
            throw new InvalidOperationException(
                $"it needs {gem.Name}, which is being made: a cycle, as {gem.Name} needs something that needs it.");
        }

        if (!_tried.Add(gem.Type)) throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");

        Result<ISettings?> settings = services.GetRequiredService<Settings>().Make(gem.Name, gem.Settings);
        if (!settings.Ok)
        {
            Debugging.Error($"Gem {gem.Name} was not made: {settings.Error}");

            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");
        }

        IGem made;
        _making.Add(gem.Type);
        try
        {
            made = (IGem)ActivatorUtilities.CreateInstance(
                services,
                gem.Type,
                settings.Value is { } given ? [given] : []);
        }
        catch (Exception ex)
        {
            // A service that is missing or was not made, and a gem that cannot start, say why in the message; anything
            // else is a bug in the gem, and its stack says where.
            Debugging.Error(
                $"Gem {gem.Name} was not made: {(ex is InvalidOperationException ? ex.Message : ex.ToString())}");

            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.", ex);
        }
        finally
        {
            _making.Remove(gem.Type);
        }

        _made.Add(made);
        if (made is ILogSink sink) Debugging.Add(sink);

        string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
        Debugging.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");

        return made;
    }

    /// <summary>
    /// Code that is no gem: where it is built, what to call it in the log, and the changes its folder has had, of
    /// which the latest loads it once it has settled.
    /// </summary>
    private sealed class CodeFile(string path, string what)
    {
        private int _changes;

        internal string Path { get; } = path;

        internal string What { get; } = what;

        /// <summary>
        /// Notes one more change to its folder: which one, to ask <see cref="IsLatest"/> of after the settle.
        /// </summary>
        internal int Note() => Interlocked.Increment(ref _changes);

        internal bool IsLatest(int change) => change == Volatile.Read(ref _changes);
    }
}
