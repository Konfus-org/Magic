using Core.Interfaces;
using Core.Utils;
using DryIoc;
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
        // Anything the host already has loaded (Core, DryIoc, ...) must be shared, never loaded
        // a second time into this context: types from two copies of the same assembly are not
        // interchangeable, and the gem would no longer implement the host's IGem.
        if (Default.Assemblies.Any(a => a.GetName().Name == assemblyName.Name))
        {
            return null;
        }

        // Resolve the gem's own dependencies (like shared libraries or framework dlls)
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null; // Fall back to the default context for host assemblies (like IGem)
    }
}

internal sealed record LoadedGem(
    IGem Instance,
    GemMetadata Metadata,
    GemLoadContext Context);

/// <summary>
/// Loads <see cref="IGem"/> instances from assemblies and initializes them via their OnLoad method.
/// </summary>
internal sealed class GemLoader
{
    private readonly IFileOperations _fileOperations;
    private readonly IContainer _container;

    public GemLoader(IFileOperations fileOperations, IContainer container)
    {
        _fileOperations = fileOperations;
        _container = container;
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

        // Get the specific type (Namespace.ClassName)
        Type? myType = assembly.GetTypes().FirstOrDefault(t => t.GetInterface(nameof(IGem)) != null);
        if (myType is null) return null;

        // Create an instance of the class, injecting host services into its constructor.
        // The gem type itself is never registered, and the cache is dropped, so the
        // container holds no reference to the gem's assembly.
        if (_container.New(myType, null, RegistrySharing.CloneAndDropCache) is not IGem myInstance) return null;

        // Call the OnLoad method of the instance; it may register services into the container
        myInstance.OnLoad(_container);

        // Load metadata from .meta next to the gem assembly
        string metaPath = Path.ChangeExtension(gemPath, ".meta");
        Result<string> readResult = await _fileOperations
            .ReadTextAsync(metaPath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!readResult.Ok) return null;
        GemMetadata meta = JsonSerializer.Deserialize<GemMetadata>(readResult.Payload);

        // Return the loaded gem
        return new LoadedGem(myInstance, meta, gemAssembly);
    }

    /// <summary>
    /// Unregisters every service implemented by a type from the gem's assembly, then unloads the assembly.
    /// Must run after the gem's OnUnload/OnReloading so the container no longer references the gem's types.
    /// </summary>
    internal void Unload(LoadedGem gem)
    {
        Assembly gemAssembly = gem.Instance.GetType().Assembly;
        ServiceRegistrationInfo[] gemServices = _container.GetServiceRegistrations()
            .Where(r => r.ImplementationType?.Assembly == gemAssembly)
            .ToArray();
        foreach (ServiceRegistrationInfo service in gemServices)
        {
            _container.Unregister(service.ServiceType, service.OptionalServiceKey, service.Factory.FactoryType, null);
            // Drop the compiled resolve delegates too, or they keep the gem's types (and so its assembly) alive
            _container.ClearCache(service.ServiceType, service.Factory.FactoryType, service.OptionalServiceKey);
        }

        gem.Context.Unload();
    }
}
