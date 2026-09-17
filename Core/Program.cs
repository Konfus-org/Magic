using Core.GemAPI;
using Core.Interfaces;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;

// Setup core services
ServiceCollection services = new();
services.AddSingleton<IFileOperations, FileOperations>();
services.AddSingleton(new Directories(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "Gems")));
services.AddSingleton<GemRegistry>();
ServiceProvider serviceProvider = services.BuildServiceProvider();

// Load gems
string gemsDirectory = serviceProvider.GetRequiredService<Directories>().Gems;
GemLoader gemLoader = new(serviceProvider.GetRequiredService<IFileOperations>());
LoadedGem[] loadedGems = await gemLoader
    .LoadAllAsync(gemsDirectory, null, CancellationToken.None)
    .ConfigureAwait(false);
foreach (GemMetadata loadedGem in loadedGems.Select(g => g.Metadata))
{
    Log.Info($"Loaded gem: {loadedGem.Name} v{loadedGem.Version} by {loadedGem.Author}");
}

// TODO: Setup windowing, rendering, asset loading, and input gems these all need to be behind interfaces

// Unload gems
foreach (LoadedGem loadedGem in loadedGems)
{
    Log.Flush(); // Flush logs before unloading gems to ensure all log messages are written
    Log.Info($"Unloading gem: {loadedGem.Metadata.Name}");
    loadedGem.Instance.OnUnload();
    loadedGem.Context.Unload();
}

await serviceProvider.DisposeAsync().ConfigureAwait(false);
