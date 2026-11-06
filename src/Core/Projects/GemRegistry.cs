using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Files;
using Magic.Files.Pipeline;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Projects;

/// <summary>
/// The gems a project lists, found and registered with the services as their class and every interface they provide,
/// but not yet made. A gem that can't be found or registered is logged, and the others go on without it.
/// </summary>
internal static class GemRegistry
{
    /// <summary>
    /// Finds the gems <paramref name="listed"/> beside Magic and registers each with <paramref name="services"/>, which
    /// already holds what the host provides: the gems to make, each after the gems its constructor takes. A name listed
    /// twice, the wrong version, an interface already provided or a cycle is refused. Each gem is made by the provider
    /// built from <paramref name="services"/>, once: asked for again after it failed, it throws.
    /// </summary>
    public static List<GemInfo> Register(IFileSystem files, ImmutableArray<string> listed, ServiceCollection services)
    {
        if (listed.Any())
            Log.Info($"Loading {string.Join(", ", listed)}.");

        // Find each listed gem beside Magic.
        List<GemInfo> found = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in listed)
        {
            // A gem is listed by its name, then @ and the version it must be where that matters.
            int at = listing.IndexOf('@', StringComparison.Ordinal);
            string name = at < 0 ? listing : listing[..at];
            string wanted = at < 0 ? string.Empty : listing[(at + 1)..];
            if (!names.Add(name))
            {
                Log.Error($"Gem {name} is listed twice; it is made once.");
                continue;
            }

            Result<GemInfo> gem = Find(files, name);
            if (!gem.Ok)
            {
                Log.Error(gem.Error);
                continue;
            }

            if (wanted.Length > 0 && !IsVersion(gem.Value.Version, wanted))
            {
                Log.Error($"Gem {name} is {gem.Value.Version.ToString(3)}, but the project asks for {wanted}.");
                continue;
            }

            found.Add(gem.Value);
        }

        // Register each with the services as its class and every interface it provides.
        List<GemInfo> registered = [];
        Dictionary<Type, GemInfo> providers = [];
        foreach (GemInfo gem in found)
        {
            if (FindClash(services, gem) is { } clash)
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
            providers[gem.Type] = gem;
            foreach (Type contract in gem.Provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(gem.Type));
                providers[contract] = gem;
            }

            registered.Add(gem);
        }

        // A gem that takes the world needs what the world is made from.
        if (providers.TryGetValue(typeof(IEcs), out GemInfo? ecs))
            providers[typeof(World)] = ecs;

        // Sort each after the gems its constructor takes, by their class or an interface they provide. A gem in a cycle,
        // or one that needs it, is logged and left out, since the provider hangs making a cycle.
        List<GemInfo> sorted = [];
        HashSet<GemInfo> placed = [];
        HashSet<GemInfo> refused = [];
        HashSet<GemInfo> visiting = [];
        foreach (GemInfo gem in registered)
        {
            Place(gem);
        }

        return sorted;

        bool Place(GemInfo gem)
        {
            if (placed.Contains(gem))
                return true;

            // A gem visited again before it is placed is in a cycle; the gem that needs it says so.
            if (refused.Contains(gem) || !visiting.Add(gem))
                return false;

            foreach (ParameterInfo parameter in gem.Type.GetConstructors()[0].GetParameters())
            {
                if (!providers.TryGetValue(parameter.ParameterType, out GemInfo? need) || need == gem || Place(need))
                    continue;

                string why = visiting.Contains(need) ? "which needs it: a cycle" : "which was not made";
                Log.Error($"Gem {gem.Name} was not made: it needs {need.Name}, {why}.");
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
    /// The gem <paramref name="name"/> from <c>Gems/&lt;name&gt;/&lt;name&gt;.dll</c> beside Magic, or why it can't be
    /// made: it must hold one <see cref="IGem"/> class with one public constructor.
    /// </summary>
    private static Result<GemInfo> Find(IFileSystem files, string name)
    {
        string path = files.Combine(AppContext.BaseDirectory, "Gems", name, name + ".dll");
        if (!files.FileExists(path))
            return Result.Failure($"Gem {name} was not found: there is no {path}.");

        GemLoadContext context = new(path, files);
        Result<Assembly> loaded = LoadAssembly(context, path, name);
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

        // What the host takes itself isn't provided, so any number of gems can be sinks, the assets say which gem reads
        // or writes a type.
        List<Type> provides = [];
        foreach (Type contract in gem.GetInterfaces())
        {
            if (contract.IsGenericType && IsHostContract(contract.GetGenericTypeDefinition()))
                continue;

            if (contract != typeof(IGem) && contract != typeof(IDisposable) && contract != typeof(ILogSink))
                provides.Add(contract);
        }

        string author = assembly.GetMetadata("Magic.Author") ?? name;
        string description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;
        Version version = assembly.GetName().Version ?? new Version(1, 0, 0);

        string folder = files.GetParent(path);
        string resources = Assets.GetResources(files, assembly, folder);
        bool hasResources = files.DirectoryExists(resources);

        return Result.Success(
            new GemInfo(
                name,
                author,
                description,
                version,
                gem,
                provides,
                hasResources ? resources : null,
                folder,
                context
            )
        );
    }

    /// <summary>
    /// Whether a gem implementing <paramref name="contract"/> hands it to the host, rather than provide it to other gems.
    /// </summary>
    private static bool IsHostContract(Type contract) =>
        contract == typeof(IAssetImporter<>) || contract == typeof(IAssetExporter<>);

    /// <summary>
    /// The assembly at <paramref name="path"/>, loaded from its file into <paramref name="context"/>.
    /// </summary>
    private static Result<Assembly> LoadAssembly(GemLoadContext context, string path, string name)
    {
        try
        {
            Result<Assembly> loaded = context.LoadFromBytes(path);

            return loaded.Ok ? loaded : Result.Failure($"Gem {name} could not be loaded from {path}: {loaded.Error}");
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
    private static string? FindClash(ServiceCollection services, GemInfo gem)
    {
        foreach (Type contract in gem.Provides)
        {
            foreach (ServiceDescriptor service in services)
            {
                // The world proxies what a gem provides, so it provides nothing itself.
                if (service.ImplementationInstance is World)
                    continue;

                // A gem is registered as its class before its interfaces, so the class is what is found.
                Type provider = service.ImplementationInstance?.GetType() ?? service.ServiceType;
                if (provider.IsAssignableTo(contract))
                    return $"Gem {gem.Name} provides {GetName(contract)}, which {provider.Name} already provides.";
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s name as C# writes it: <c>IAssetImporter&lt;Texture&gt;</c>, not <c>IAssetImporter`1</c>.
    /// </summary>
    private static string GetName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];

        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), GetName))}>";
    }
}
