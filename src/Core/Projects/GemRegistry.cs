using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Files;
using Magic.Graphics;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Projects;

/// <summary>
/// Finds the gems that a project lists and registers their classes, parts and interfaces with the services, without
/// making them.
/// </summary>
/// <remarks>
/// A gem that cannot be found or registered is logged, and the others go on without it.
/// </remarks>
internal static class GemRegistry
{
    /// <summary>
    /// The contracts that the host collects from gems, so no gem provides them. A generic contract is held by its
    /// definition.
    /// </summary>
    /// <remarks>
    /// Any number of gems can be sinks or hook into domain loading, and the assets say which gem reads or writes a
    /// type.
    /// </remarks>
    private static readonly HashSet<Type> _hostContracts =
    [
        typeof(IGem),
        typeof(IDisposable),
        typeof(ILogSink),
        typeof(IHookIntoDomainLoading),
        typeof(IRender),
        typeof(IAssetImporter<>),
        typeof(IAssetExporter<>),
        typeof(ISettings),
        typeof(IRender<,>),
        typeof(IRenderFrom<>),
        typeof(IRenderInto<>),
    ];

    /// <summary>
    /// Finds the gems <paramref name="listed"/> beside Magic, registers each with <paramref name="services"/>, and
    /// gives them in the order to make them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="services"/> already holds what the host provides. Each gem comes after the gems that its code
    /// references and whose parts its constructor takes.
    /// </para>
    /// <para>
    /// It refuses a name listed two times, the wrong version, a referenced gem that is not listed, an interface already
    /// provided, and a cycle. The provider built from <paramref name="services"/> makes each gem one time; an ask after
    /// a failure throws.
    /// </para>
    /// </remarks>
    public static List<RegisteredGem> Register(
        IFileSystem files,
        ImmutableArray<string> listed,
        ServiceCollection services
    )
    {
        if (listed.Any())
            Log.Info($"Loading {string.Join(", ", listed)}.");

        // Make each listed gem's context before any loads, so a gem's code finds a gem it references in any list order.
        Dictionary<string, GemLoadContext> contexts = new(StringComparer.OrdinalIgnoreCase);
        List<(string Name, string Wanted)> named = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in listed)
        {
            // A listing is the gem's name, then optionally @ and the version it must have.
            int at = listing.IndexOf('@', StringComparison.Ordinal);
            string name = at < 0 ? listing : listing[..at];
            if (!names.Add(name))
            {
                Log.Error($"Gem {name} is listed twice; it is made once.");
                continue;
            }

            string path = GetPath(files, name);
            if (!files.FileExists(path))
            {
                Log.Error($"Gem {name} was not found: there is no {path}.");
                continue;
            }

            contexts[name] = new GemLoadContext(path, files, contexts);
            named.Add((name, at < 0 ? string.Empty : listing[(at + 1)..]));
        }

        // Register each gem with the services as its class, its parts and each interface they provide.
        List<RegisteredGem> registered = [];
        Dictionary<Type, RegisteredGem> providers = [];
        foreach ((string name, string wanted) in named)
        {
            Result<RegisteredGem> found = Find(files, name, contexts);
            if (!found.Ok)
            {
                Log.Error(found.Error);
                continue;
            }

            RegisteredGem gem = found.Value;
            if (wanted.Length > 0 && !IsVersion(gem.Version, wanted))
            {
                Log.Error($"Gem {name} is {gem.Version.ToString(3)}, but the project asks for {wanted}.");
                continue;
            }

            if (FindClash(services, providers, gem) is { } clash)
            {
                Log.Error(clash);
                continue;
            }

            // The provider makes a factory's service again after it threw, which would make a failed gem twice.
            bool isTried = false;
            services.AddSingleton(
                gem.Type,
                provider =>
                {
                    if (isTried)
                        throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");

                    isTried = true;
                    return ActivatorUtilities.CreateInstance(provider, gem.Type);
                }
            );

            // Each part is its property's value on the made gem. Settings are the exception: the gem is made with
            // them, as set before over their defaults.
            foreach (PropertyInfo part in gem.Parts)
            {
                Type type = part.PropertyType;
                providers[type] = gem;
                if (type.IsAssignableTo(typeof(ISettings)))
                {
                    services.AddTransient(type, provider => provider.GetRequiredService<Settings>().Get(type));
                    continue;
                }

                services.AddSingleton(
                    type,
                    provider =>
                        part.GetValue(provider.GetRequiredService(gem.Type))
                        ?? throw new InvalidOperationException($"{gem.Name}'s {part.Name} is null.")
                );
            }

            // Each interface is the part that implements it.
            foreach (Type contract in gem.Provides)
            {
                Type type = gem.PartTypes.First(part => part.IsAssignableTo(contract));
                services.AddSingleton(contract, provider => provider.GetRequiredService(type));
                providers[contract] = gem;
            }

            registered.Add(gem);
        }

        // A gem that takes the world needs what the world is made from.
        if (providers.TryGetValue(typeof(IEcs), out RegisteredGem? ecs))
            providers[typeof(World)] = ecs;

        // Sort each gem after the gems it needs: those its code references, and those whose part its constructor takes.
        // A gem in a cycle, or one that needs it, is logged and left out, as the provider hangs on a cycle.
        Dictionary<string, RegisteredGem> byName = registered.ToDictionary(
            gem => gem.Name,
            StringComparer.OrdinalIgnoreCase
        );
        List<RegisteredGem> sorted = [];
        HashSet<RegisteredGem> placed = [];
        HashSet<RegisteredGem> refused = [];
        HashSet<RegisteredGem> visiting = [];
        foreach (RegisteredGem gem in registered)
        {
            Place(gem);
        }

        return sorted;

        bool Place(RegisteredGem gem)
        {
            if (placed.Contains(gem))
                return true;

            // A gem visited again before it is placed is in a cycle; the gem that needs it says so.
            if (refused.Contains(gem) || !visiting.Add(gem))
                return false;

            List<string> needs = [.. gem.References];
            foreach (ParameterInfo parameter in gem.Type.GetConstructors()[0].GetParameters())
            {
                if (providers.TryGetValue(parameter.ParameterType, out RegisteredGem? provider))
                    needs.Add(provider.Name);
            }

            foreach (string name in needs)
            {
                RegisteredGem? need = byName.GetValueOrDefault(name);
                if (need == gem || (need is not null && Place(need)))
                    continue;

                string why =
                    need is not null && visiting.Contains(need) ? "which needs it: a cycle" : "which was not made";
                Log.Error($"Gem {gem.Name} was not made: it needs {name}, {why}.");
                visiting.Remove(gem);
                refused.Add(gem);
                return false;
            }

            visiting.Remove(gem);
            placed.Add(gem);
            sorted.Add(gem);
            return true;
        }
    }

    /// <summary>
    /// Gets the path of the gem <paramref name="name"/>: <c>Gems/&lt;name&gt;/&lt;name&gt;.dll</c> beside Magic.
    /// </summary>
    internal static string GetPath(IFileSystem files, string name) =>
        files.Combine(AppContext.BaseDirectory, "Gems", name, name + ".dll");

    /// <summary>
    /// Gets the name of the gem that declares <paramref name="type"/>: its assembly's name, or the type's name when the
    /// assembly has none.
    /// </summary>
    internal static string GetGemName(Type type) => type.Assembly.GetName().Name ?? type.Name;

    /// <summary>
    /// Loads the listed gem <paramref name="name"/> into its context of <paramref name="contexts"/>, or gives why it
    /// cannot be made.
    /// </summary>
    /// <remarks>
    /// A gem must hold one <see cref="IGem"/> class with one public constructor, and the project must list each gem
    /// that it references. The class's public instance properties are its parts, and the gem provides what they
    /// implement.
    /// </remarks>
    private static Result<RegisteredGem> Find(
        IFileSystem files,
        string name,
        Dictionary<string, GemLoadContext> contexts
    )
    {
        string path = GetPath(files, name);
        Result<Assembly> loaded;
        try
        {
            loaded = contexts[name].LoadGem();
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            loaded = Result.Failure(ex.Message);
        }

        if (!loaded.Ok)
            return Result.Failure($"Gem {name} could not be loaded from {path}: {loaded.Error}");

        // A referenced assembly named like a listed gem is that gem. One named like a gem beside Magic, and not in this
        // gem's folder, is a gem the project must list.
        Assembly assembly = loaded.Value;
        string folder = files.GetParent(path);
        List<string> references = [];
        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is not { } referenced)
                continue;

            bool isCarried = files.FileExists(files.Combine(folder, referenced + ".dll"));
            if (contexts.ContainsKey(referenced))
                references.Add(referenced);
            else if (!isCarried && files.FileExists(GetPath(files, referenced)))
                return Result.Failure(
                    $"Gem {name} was not made: it needs {referenced}, which the project doesn't list."
                );
        }

        List<Type> gems = [];
        foreach (Type type in assembly.GetLoadableTypes())
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

        List<PropertyInfo> parts = [.. gem.GetProperties(BindingFlags.Public | BindingFlags.Instance)];
        List<Type> provides = [];
        foreach (Type contract in parts.SelectMany(part => part.PropertyType.GetInterfaces()))
        {
            if (!_hostContracts.Contains(contract.IsGenericType ? contract.GetGenericTypeDefinition() : contract))
                provides.Add(contract);
        }

        string author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? name;
        string description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;
        Version version = assembly.GetName().Version ?? new Version(1, 0, 0);

        string resources = AssetManager.GetResources(files, assembly, folder);
        bool hasResources = files.DirectoryExists(resources);

        return Result.Success(
            new RegisteredGem(
                name,
                author,
                description,
                version,
                gem,
                parts,
                provides,
                references,
                hasResources ? resources : null,
                folder,
                contexts[name]
            )
        );
    }

    /// <summary>
    /// Whether <paramref name="version"/> matches each part that <paramref name="wanted"/> gives, as <c>1</c>,
    /// <c>1.2</c> or <c>1.2.3</c>. Text other than one to four numbers matches nothing.
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
    /// Gets why <paramref name="gem"/> is refused when <paramref name="services"/> already provides one of its
    /// interfaces; null when none does.
    /// </summary>
    /// <remarks>
    /// The reason names the providing gem of <paramref name="providers"/>, or the host's class.
    /// </remarks>
    private static string? FindClash(
        ServiceCollection services,
        Dictionary<Type, RegisteredGem> providers,
        RegisteredGem gem
    )
    {
        foreach (Type contract in gem.Provides)
        {
            foreach (ServiceDescriptor service in services)
            {
                // The world proxies what a gem provides, so it provides nothing itself.
                if (service.ImplementationInstance is World)
                    continue;

                // A part is registered as its class before its interfaces, so the class is what is found.
                Type provider = service.ImplementationInstance?.GetType() ?? service.ServiceType;
                if (!provider.IsAssignableTo(contract))
                    continue;

                string owner = providers.TryGetValue(provider, out RegisteredGem? other)
                    ? $"gem {other.Name}"
                    : provider.Name;
                return $"Gem {gem.Name} provides {GetName(contract)}, which {owner} already provides.";
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the name of <paramref name="type"/> as C# writes it: <c>IAssetImporter&lt;Texture&gt;</c>, not
    /// <c>IAssetImporter`1</c>.
    /// </summary>
    private static string GetName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];

        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), GetName))}>";
    }
}
