using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Internal;

/// <summary>
/// Loads the gems a project lists into the service provider <see cref="Engine"/> builds, and makes them once it has.
/// Each gem's assembly (<see cref="GemAssembly"/>) is registered as its class and as every interface it provides; one
/// that provides what Core or a gem listed before it provides is refused, as is a name listed twice, and one that is
/// not the version the project asks for (<see cref="Project.Gems"/>). A gem's assembly is taken in as it loads
/// (<see cref="Assemblies"/>), so files can name its components and scripts before any gem is made, and the folder of
/// its own assets is added to the assets (<see cref="Assets.Register"/>), which read it while the next gem loads. The
/// provider makes each gem after whatever its constructor asks for, so <see cref="Load"/> returns the gems in
/// dependency order. A constructor parameter that is an <see cref="ISettings"/> is the gem's settings object, made by
/// <see cref="Settings"/> as the gem is. A gem that is an <see cref="ILogSink"/> is added to the log once it is made,
/// instead of provided, and one that is an <see cref="IPreloader"/> is given to the world by whoever made the gems.
/// What is registered for a gem, its assets' folder, its assembly and its place on the log, is kept with it, to end
/// with it. Whatever goes wrong with one gem is logged, and the others are made without it.
/// </summary>
internal sealed class GemLoader : IDisposable
{
    private readonly List<CreatedGem> _created = [];
    private readonly HashSet<Type> _tried = [];

    /// <summary>
    /// What is registered for each gem, by its class: its assets' folder and its assembly as it loads, and its place on
    /// the log once it is made, when it is a sink.
    /// </summary>
    private readonly Dictionary<Type, SubscriptionList> _registered = [];

    /// <summary>
    /// The gems whose constructors are running, so one asked for by what it needs is told it is a cycle.
    /// </summary>
    private readonly HashSet<Type> _creating = [];
    private readonly List<GemAssembly> _gemAssemblies = [];

    /// <summary>
    /// Loads the gems the project lists, registers them with <paramref name="services"/>, which already holds what
    /// Core provides, and adds each one's own assets to <paramref name="assets"/> as it loads it.
    /// </summary>
    internal GemLoader(
        ServiceCollection services,
        IFileSystem files,
        Project project,
        Assemblies assemblies,
        Assets assets)
    {
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in project.Gems)
        {
            // A gem is listed by its name, then @ and the version it must be where that matters.
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

            // Registered as its class, made once, and as each interface it provides, which hands out that one gem.
            services.AddSingleton(gem.Type, provider => CreateGem(provider, gem));
            foreach (Type contract in gem.Provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(gem.Type));
            }

            _gemAssemblies.Add(gem);
            SubscriptionList registered = new();
            registered.Add(assets.Register(gem.Resources));
            registered.Add(assemblies.Register(gem.Path, gem.Assembly));
            _registered[gem.Type] = registered;
        }
    }

    /// <summary>
    /// Ends what is registered for every gem, the gems that were not made among them.
    /// </summary>
    public void Dispose()
    {
        foreach (SubscriptionList registered in _registered.Values)
        {
            registered.Dispose();
        }
    }

    /// <summary>
    /// Makes every gem that was registered, in the order they are listed, each after whatever its constructor asks for;
    /// the gems, in the order they were made, each with what is registered for it, to end before it is disposed. A gem
    /// that could not be made has said why, and is left out.
    /// </summary>
    internal List<CreatedGem> Load(ServiceProvider container)
    {
        foreach (GemAssembly gem in _gemAssemblies)
        {
            try
            {
                container.GetRequiredService(gem.Type);
            }
            catch (InvalidOperationException)
            {
                // CreateGem has logged why the gem was not created.
            }
        }

        return _created;
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
    /// The service provider's factory for a gem: creates it from its settings object and the services its constructor
    /// asks for, which the provider makes first, adds it to what was made, and adds it to the log if it is an
    /// <see cref="ILogSink"/>. Why it could not be made, settings that do not fit it among the reasons, is logged here,
    /// against the gem, and the gems that need it are told only that it was not. A gem is tried once; one asked for
    /// while its constructor runs, by something it needs, is a cycle, which is said.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on.")]
    private IGem CreateGem(IServiceProvider services, GemAssembly gem)
    {
        if (_creating.Contains(gem.Type))
        {
            throw new InvalidOperationException(
                $"it needs {gem.Name}, which is being made: a cycle, as {gem.Name} needs something that needs it.");
        }

        if (!_tried.Add(gem.Type)) throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");

        Result<ISettings?> settings = services.GetRequiredService<Settings>().Create(gem.Name, gem.Settings);
        if (!settings.Ok)
        {
            Debugging.Error($"Gem {gem.Name} was not made: {settings.Error}");

            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");
        }

        IGem made;
        _creating.Add(gem.Type);
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
            _creating.Remove(gem.Type);
        }

        SubscriptionList registered = _registered[gem.Type];
        if (made is ILogSink sink) registered.Add(Debugging.Register(sink));

        _created.Add(new CreatedGem(made, gem.Name, registered));

        string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
        Debugging.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");

        return made;
    }

    /// <summary>
    /// A gem as it was made: the gem, the name the project lists it by, and what is registered for it, to end before it
    /// is disposed.
    /// </summary>
    internal readonly record struct CreatedGem(IGem Gem, string Name, SubscriptionList Registrations);
}
