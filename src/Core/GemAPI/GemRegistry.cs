using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Files;
using Magic.Pipelines;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.GemAPI;

/// <summary>
/// The gems a project lists, found and registered with the services as their class and every interface they provide,
/// but not yet made. A gem that can't be found or registered is logged, and the others go on without it.
/// </summary>
internal sealed class GemRegistry
{
    private readonly Dictionary<string, GemInfo> _byName;

    /// <summary>
    /// Finds the <paramref name="gemsToLoad"/> beside Magic and registers each with <paramref name="services"/>, which
    /// already holds what the host provides. A name listed twice, the wrong version, or an interface already provided
    /// is refused. Each gem is made by the provider built from <paramref name="services"/>, once: asked for again after
    /// it failed, it throws.
    /// </summary>
    public GemRegistry(ServiceCollection services, IFileSystem files, ImmutableArray<string> gemsToLoad)
    {
        if (gemsToLoad.Any())
            Log.Info($"Loading {string.Join(", ", gemsToLoad)}.");

        List<GemInfo> registered = [];
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in gemsToLoad)
        {
            // A gem is listed by its name, then @ and the version it must be where that matters.
            int at = listing.IndexOf('@', StringComparison.Ordinal);
            string name = at < 0 ? listing : listing[..at];
            string wanted = at < 0 ? string.Empty : listing[(at + 1)..];
            if (!listed.Add(name))
            {
                Log.Error($"Gem {name} is listed twice; it is made once.");
                continue;
            }

            Result<GemInfo> found = Find(files, name);
            if (!found.Ok)
            {
                Log.Error(found.Error);
                continue;
            }

            GemInfo gem = found.Value;
            if (wanted.Length > 0 && !IsVersion(gem.Version, wanted))
            {
                Log.Error($"Gem {name} is {gem.Version.ToString(3)}, but the project asks for {wanted}.");
                continue;
            }

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
            foreach (Type contract in gem.Provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(gem.Type));
            }

            registered.Add(gem);
        }

        Gems = GetNeeds(registered);
        _byName = Gems.ToDictionary(gem => gem.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The gems registered, in the order the project lists them.
    /// </summary>
    public IReadOnlyList<GemInfo> Gems { get; }

    /// <summary>
    /// The gem registered as <paramref name="name"/>; null when there is none.
    /// </summary>
    public GemInfo? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// The gem <paramref name="name"/> from <c>Gems/&lt;name&gt;/&lt;name&gt;.dll</c> beside Magic, or why it can't be
    /// made: it must hold one <see cref="IGem"/> class with one public constructor.
    /// </summary>
    private static Result<GemInfo> Find(IFileSystem files, string name)
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

        // What the host takes itself isn't provided, so any number of gems can be sinks, the assets say which gem reads
        // a type, and only the engine runs a pipeline.
        List<Type> provides = [];
        List<Type> reads = [];
        List<Type> joins = [];
        foreach (Type contract in gem.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAssetLoader<>))
                reads.Add(contract.GetGenericArguments()[0]);
            else if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IPipeline<>))
                joins.Add(contract.GetGenericArguments()[0]);
            else if (contract != typeof(IGem) && contract != typeof(IDisposable) && contract != typeof(ILogSink))
                provides.Add(contract);
        }

        string author = assembly.GetMetadata("Magic.Author") ?? name;
        string description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;
        Version version = assembly.GetName().Version ?? new Version(1, 0, 0);

        string resources = AssetPipeline.GetResources(files, assembly, files.GetParent(path));
        bool hasResources = files.DirectoryExists(resources);

        return Result.Success(
            new GemInfo(
                name,
                author,
                description,
                version,
                gem,
                provides,
                reads,
                joins,
                [],
                hasResources ? resources : null
            )
        );
    }

    /// <summary>
    /// <paramref name="gems"/>, each with the names of the others its constructor asks for, by their class or an
    /// interface they provide.
    /// </summary>
    private static List<GemInfo> GetNeeds(List<GemInfo> gems)
    {
        Dictionary<Type, string> providers = [];
        foreach (GemInfo gem in gems)
        {
            providers[gem.Type] = gem.Name;
            foreach (Type contract in gem.Provides)
            {
                providers[contract] = gem.Name;
            }
        }

        // A gem that takes the world needs what the world is made from.
        if (providers.TryGetValue(typeof(IEcs), out string? ecs))
            providers[typeof(World)] = ecs;

        List<GemInfo> withNeeds = [];
        foreach (GemInfo gem in gems)
        {
            List<string> needs = [];
            foreach (ParameterInfo parameter in gem.Type.GetConstructors()[0].GetParameters())
            {
                if (!providers.TryGetValue(parameter.ParameterType, out string? provider))
                    continue;

                if (provider != gem.Name && !needs.Contains(provider))
                    needs.Add(provider);
            }

            withNeeds.Add(gem with { Needs = needs });
        }

        return withNeeds;
    }

    /// <summary>
    /// The assembly at <paramref name="path"/>, loaded from its file into a <see cref="GemLoadContext"/> of its own.
    /// </summary>
    private static Result<Assembly> LoadAssembly(IFileSystem files, string path, string name)
    {
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
    /// <paramref name="type"/>'s name as C# writes it: <c>IAssetLoader&lt;Texture&gt;</c>, not <c>IAssetLoader`1</c>.
    /// </summary>
    private static string GetName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];

        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), GetName))}>";
    }
}
