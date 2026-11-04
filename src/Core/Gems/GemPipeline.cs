using Magic.Assets.Pipeline;
using Magic.Pipes;
using Magic.Worlds;

namespace Magic.Gems;

/// <summary>
/// The gems' side of a run: has what their constructors need made ready and makes them; at the end unloads them.
/// </summary>
internal sealed class GemPipeline(
    GemRegistry gems,
    IServiceProvider provider,
    App app,
    AssetDatabase assets,
    World world,
    Pipelines pipelines
) : IPipeline<AppStart>, IPipeline<AppEnd>
{
    private IReadOnlyList<LoadedGem> _gems = [];

    public ValueTask<AppStart> RunAsync(AppStart state, CancellationToken cancel)
    {
        pipelines.Get<GemsFound>().Run(new GemsFound(gems), cancel);
        _gems = GemLoader.Load(provider, gems, app, assets, pipelines, world);

        return ValueTask.FromResult(state);
    }

    public ValueTask<AppEnd> RunAsync(AppEnd state, CancellationToken cancel)
    {
        GemLoader.Unload(_gems);

        return ValueTask.FromResult(state);
    }
}
