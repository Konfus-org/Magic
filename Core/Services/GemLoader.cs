using Core.Contexts;
using Core.Contexts;
using Core.Interfaces;
using Core.Utils;
using DryIoc;
using System.Reflection;
using System.Text.Json;

namespace Core.Services;

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

    internal async Task<GemContext[]> LoadAllAsync(string directory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Result<string[]> gemFiles = await _fileOperations.ReadDirectoryAsync(directory, "*.dll", progress, cancellationToken);
        GemContext[] loadedGems = new GemContext[gemFiles.Payload.Length];
        foreach (string gemFile in gemFiles.Payload)
        {
            GemContext? loadedGem = await LoadSingleAsync(gemFile, progress, cancellationToken).ConfigureAwait(false);
            if (loadedGem is not null)
            {
                loadedGems[Array.IndexOf(gemFiles.Payload, gemFile)] = loadedGem;
            }
        }
        if (loadedGems.All(g => g is null))
            return Array.Empty<GemContext>();
        return loadedGems;
    }

    internal async Task<GemContext?> LoadSingleAsync(string gemPath, IProgress<double>? progress, CancellationToken cancellationToken)
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
        return new GemContext(myInstance, meta, gemAssembly);
    }

    /// <summary>
    /// Unregisters every service implemented by a type from the gem's assembly, then unloads the assembly.
    /// Must run after the gem's OnUnload/OnReloading so the container no longer references the gem's types.
    /// </summary>
    internal void Unload(GemContext gem)
    {
        Assembly gemAssembly = gem.Loaded.GetType().Assembly;
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
