using Magic.Assets.Pipeline;

namespace Magic.Pipelines;

/// <summary>
/// Lands what the assets finished, so they hold still for the frame.
/// </summary>
internal sealed class AssetsPipeline(AssetManager assets) : IPipeline<HostPump>
{
    public ValueTask<HostPump> RunAsync(HostPump state, CancellationToken cancel)
    {
        assets.Pump();

        return ValueTask.FromResult(state);
    }
}
