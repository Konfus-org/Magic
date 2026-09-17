using Core.Contexts;
using Core.Interfaces;
using Core.Services;
using DryIoc;

// Setup core services. The container stays open: gems register into it later.
// Reuse is explicit on purpose: the default (transient) is the safe one for anything a gem registers.
using Container container = new();
container.Register<IFileOperations, FileOperations>(Reuse.Singleton);
container.RegisterInstance(new Directories(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "Gems")));
container.Register<GemRegistry>(Reuse.Singleton);
container.Register<GemLoader>(Reuse.Singleton);

// Load gems
string gemsDirectory = container.Resolve<Directories>().Gems;
GemLoader gemLoader = container.Resolve<GemLoader>();
GemContext[] loadedGems = await gemLoader
    .LoadAllAsync(gemsDirectory, null, CancellationToken.None)
    .ConfigureAwait(false);
foreach (GemMetadata loadedGem in loadedGems.Select(g => g.Metadata))
    Log.Info($"Loaded gem: {loadedGem.Name} v{loadedGem.Version} by {loadedGem.Author}");

// TODO: Setup windowing, rendering, asset loading, and input gems these all need to be behind interfaces

// Unload gems
foreach (GemContext loadedGem in loadedGems)
{
    Log.Flush(); // Flush logs before unloading gems to ensure all log messages are written
    Log.Info($"Unloading gem: {loadedGem.Metadata.Name}");
    loadedGem.Loaded.OnUnload();
    gemLoader.Unload(loadedGem);
}
