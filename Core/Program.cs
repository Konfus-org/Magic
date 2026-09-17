using Core.Contexts;
using Core.Interfaces;
using Core.Services;
using DryIoc;

// Setup core services. The container stays open: gems register into it later.
using Container container = new(rules => rules.WithDefaultReuse(Reuse.Singleton));
container.Register<IFileOperations, FileOperations>();
container.RegisterInstance(new Directories(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "Gems")));
container.Register<GemRegistry>();
container.Register<GemLoader>();

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
