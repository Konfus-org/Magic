using Core.Interfaces;
using Core.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Core.GemAPI;

internal sealed class GemLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // Passing isCollectible: true is critical for GC collection
    public GemLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    [RequiresUnreferencedCode("Calls System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath(String)")]
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Resolve dependencies (like shared libraries or framework dlls)
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null; // Fall back to the default context for host assemblies (like IGem)
    }
}

internal sealed record LoadedGem(
    // The actual instance of the gem, which implements IGem
    IGem Instance,
    // Gems can have metadata associated with them, which is loaded from a .meta file next to the gem assembly
    GemMetadata Metadata,
    // Services provided by the gem, if any
    ServiceProvider Services,
    // The AssemblyLoadContext that loaded the gem assembly, which allows for unloading the gem later
    GemLoadContext Context);

/// <summary>
/// Loads <see cref="IGem"/> instances from assemblies and initializes them via their OnLoad method.
/// </summary>
internal sealed class GemLoader
{
    private readonly IFileOperations _fileOperations;
    private readonly IServiceProvider _rootProvider;

    internal GemLoader(IFileOperations fileOperations, IServiceProvider rootProvider)
    {
        _fileOperations = fileOperations;
        _rootProvider = rootProvider;
    }

    internal async Task<LoadedGem[]> LoadAllAsync(string directory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Result<string[]> gemFiles = await _fileOperations.ReadDirectoryAsync(directory, "*.dll", progress, cancellationToken);
        LoadedGem[] loadedGems = new LoadedGem[gemFiles.Payload.Length];
        foreach (string gemFile in gemFiles.Payload)
        {
            LoadedGem? loadedGem = await LoadSingleAsync(gemFile, progress, cancellationToken).ConfigureAwait(false);
            if (loadedGem is not null)
            {
                loadedGems[Array.IndexOf(gemFiles.Payload, gemFile)] = loadedGem;
            }
        }
        if (loadedGems.All(g => g is null))
            return Array.Empty<LoadedGem>();
        return loadedGems;
    }

    internal async Task<LoadedGem?> LoadSingleAsync(string gemPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Load the assembly from a file path
        GemLoadContext gemAssembly = new(gemPath);
        Assembly assembly = gemAssembly.LoadFromAssemblyPath(gemPath);
        Type[] assemblyTypes = assembly.GetTypes();

        // Get the specific type (Namespace.ClassName)
        Type? myType = assemblyTypes.FirstOrDefault(t => t.GetInterface(nameof(IGem)) != null);
        if (myType is null) return null;

        // Search for services provided by the gem and register them in a new ServiceCollection
        ServiceCollection services = new();
        assemblyTypes.ForEach(t =>
        {
            if (t.GetCustomAttribute<ProvidesServiceAttribute>() is { } providerAtt)
            {
                if (providerAtt.IsSingleton)
                {
                    ProvidesServiceAttribute? attr = t.GetCustomAttribute<ProvidesServiceAttribute>();
                    if (attr is not null)
                    {
                        services.AddSingleton(attr.ServiceType, t);
                    }
                }
                else
                {
                    ProvidesServiceAttribute? attr = t.GetCustomAttribute<ProvidesServiceAttribute>();
                    if (attr is not null)
                    {
                        services.AddTransient(attr.ServiceType, t);
                    }
                }

            }
        });


        // Create an instance of the class
        IGem? myInstance = Activator.CreateInstance(myType) as IGem;
        if (myInstance is null) return null;

        // Call the OnLoad method of the instance
        myInstance.OnLoad();

        // Load metadata from .meta next to the gem assembly
        string metaPath = Path.ChangeExtension(gemPath, ".meta");
        Result<string> readResult = await _fileOperations
            .ReadTextAsync(metaPath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!readResult.Ok) return null;
        GemMetadata restored = JsonSerializer.Deserialize<GemMetadata>(readResult.Payload);

        // Return the loaded gem
        return new LoadedGem(myInstance, restored, gemAssembly);
    }
}
