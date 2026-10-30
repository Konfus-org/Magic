using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.GemAPI;

/// <summary>
/// Loads the gems a project lists and makes them, each after the gems its constructor asks for. A gem that can't be
/// loaded or made is logged, and the others go on without it.
/// Also provides an API to unload gems.
/// </summary>
public static class GemLoader
{
    /// <summary>
    /// Loads the <paramref name="gemsToLoad"/>, registers each with <paramref name="services"/>, which already holds what
    /// the host provides, adds each one's <c>Resources/</c> and asset types to <paramref name="assets"/> and indexes
    /// them, and makes them: the gems made, in dependency order. A gem that reads an asset type reads it for
    /// <paramref name="assets"/> from when it is made. End them with <see cref="Unload"/>, which also frees the gems'
    /// assemblies.
    /// </summary>
    public static IReadOnlyList<LoadedGem> Load(
        ServiceCollection services,
        IFileSystem files,
        ImmutableArray<string> gemsToLoad,
        AssetManager assets
    )
    {
        if (gemsToLoad.Any())
            Log.Info($"Loading {string.Join(", ", gemsToLoad)}.");

        // The gems made, in the order their constructors finished, which is dependency order; those tried, so a failed
        // one isn't made twice; and those being made, so one asked for again is a cycle, on which the provider hangs.
        List<LoadedGem> created = [];
        HashSet<Type> tried = [];
        HashSet<Type> creating = [];
        List<GemInfo> gems = Register(
            services,
            files,
            gemsToLoad,
            (scope, gem) => CreateGem(scope, gem, assets, created, tried, creating)
        );

        // Every asset directory is indexed before any gem is made, so a constructor can find assets.
        foreach (GemInfo gem in gems)
        {
            assets.AddAssetDirectory(gem.Resources);
            assets.AddAssetTypes(gem.Type.Assembly);
        }

        assets.Index();

        // Never disposed, since its singletons are the gems, which the caller owns; dropped when this returns, so it
        // keeps no gem from unloading.
        IServiceProvider provider = services.BuildServiceProvider();
        foreach (GemInfo gem in gems)
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

        return created;
    }

    public static void Unload(IReadOnlyList<LoadedGem> gems)
    {
        // Last made, first ended, so no gem outlives one it was made after.
        for (int i = gems.Count - 1; i >= 0; i--)
        {
            Log.Info($"Unloading {gems[i].Name}.");
            gems[i].Dispose();
        }
    }

    /// <summary>
    /// The gem <paramref name="name"/> from <c>Gems/&lt;name&gt;/&lt;name&gt;.dll</c> beside Magic, or why it can't be
    /// made: it must hold one <see cref="IGem"/> class with one public constructor.
    /// </summary>
    private static Result<GemInfo> LoadGem(IFileSystem files, string name)
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

        // What the host takes itself isn't provided, so any number of gems can be sinks, and the assets say which gem
        // reads a type.
        List<Type> provides = [];
        foreach (Type contract in gem.GetInterfaces())
        {
            bool isLoader = contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAssetLoader<>);
            if (
                !isLoader
                && contract != typeof(IGem)
                && contract != typeof(IDisposable)
                && contract != typeof(ILogSink)
            )
                provides.Add(contract);
        }

        string author = assembly.GetMetadata("Magic.Author") ?? name;
        string description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;
        Version version = assembly.GetName().Version ?? new Version(1, 0, 0);

        string resources = AssetManager.GetResources(files, assembly, files.GetParent(path));

        return Result.Success(new GemInfo(name, author, description, version, gem, provides, resources));
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
    /// Loads the gems and registers each with <paramref name="services"/> as its class
    /// and every interface it provides, made by <paramref name="create"/>. A name listed twice, the wrong version, or
    /// an interface already provided is refused.
    /// </summary>
    private static List<GemInfo> Register(
        ServiceCollection services,
        IFileSystem files,
        ImmutableArray<string> gems,
        Func<IServiceProvider, GemInfo, IGem> create
    )
    {
        List<GemInfo> registered = [];
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string listing in gems)
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

            Result<GemInfo> loaded = LoadGem(files, name);
            if (!loaded.Ok)
            {
                Log.Error(loaded.Error);
                continue;
            }

            GemInfo gem = loaded.Value;
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

            services.AddSingleton(gem.Type, provider => create(provider, gem));
            foreach (Type contract in gem.Provides)
            {
                services.AddSingleton(contract, provider => provider.GetRequiredService(gem.Type));
            }

            registered.Add(gem);
        }

        return registered;
    }

    /// <summary>
    /// The provider's factory for <paramref name="gem"/>, which adds it to <paramref name="created"/>, on the log when
    /// it is a sink, and reading for <paramref name="assets"/> the types it reads. Why it couldn't be made is logged
    /// here, once; the gems that need it are told only that it wasn't.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    private static IGem CreateGem(
        IServiceProvider services,
        GemInfo gem,
        AssetManager assets,
        List<LoadedGem> created,
        HashSet<Type> tried,
        HashSet<Type> creating
    )
    {
        if (creating.Contains(gem.Type))
            throw new InvalidOperationException($"it needs {gem.Name}, which needs it: a cycle.");

        if (!tried.Add(gem.Type))
            throw new InvalidOperationException($"it needs {gem.Name}, which was not made.");

        IGem made;
        creating.Add(gem.Type);
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
            creating.Remove(gem.Type);
        }

        LoadedGem loaded = new(made, gem.Name, gem.Version, gem.Author, gem.Description);
        if (made is ILogSink sink)
            loaded.Subscriptions.Add(Log.Register(sink));

        loaded.Subscriptions.Add(assets.AddLoader(made));

        created.Add(loaded);

        string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
        Log.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");

        return made;
    }

    /// <summary>
    /// A gem's assembly as loaded, and what making the gem takes.
    /// </summary>
    /// <param name="Name">The gem's name, as the project lists it.</param>
    /// <param name="Author">Who made it; its name when its project says nothing.</param>
    /// <param name="Description">What it is, in a sentence; empty when its project says nothing.</param>
    /// <param name="Version">Its version; 1.0.0 when its project says nothing.</param>
    /// <param name="Type">Its one <see cref="IGem"/> class.</param>
    /// <param name="Provides">The interfaces it provides to other gems.</param>
    /// <param name="Resources">The folder of its own assets.</param>
    private readonly record struct GemInfo(
        string Name,
        string Author,
        string Description,
        Version Version,
        Type Type,
        List<Type> Provides,
        string Resources
    );
}
