using Magic.Pipelines;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The assets' side of a run: indexing what is under the asset directories before any gem is made, and between frames
/// taking in what the assets were asked and what finished, when there is any. Between frames, so the assets hold still for
/// the frame.
/// </summary>
internal sealed class AssetPipeline(AssetDatabase assets, PipelineCoordinator pipelines)
    : IPipeline<AppStart>,
        IPipeline<ServicePump>,
        IPipeline<AssetsChangeList>
{
    public ValueTask<AppStart> RunAsync(AppStart state, CancellationToken cancel)
    {
        assets.Index();

        return ValueTask.FromResult(state);
    }

    public ValueTask<ServicePump> RunAsync(ServicePump state, CancellationToken cancel)
    {
        AssetsChangeList changes = assets.Changes;
        AssetSnapshot snapshot = assets.Snapshot;
        if (changes.HasChanges || !snapshot.IsLoadingEnabled || !snapshot.Touched.IsEmpty)
        {
            pipelines.Run(changes, cancel);
            changes.EndRun();
        }

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Takes in what changed, in one go, so what is read holds still until it is all in. Waiters are told last, so the
    /// snapshot they read has what they waited for.
    /// </summary>
    public ValueTask<AssetsChangeList> RunAsync(AssetsChangeList state, CancellationToken cancel)
    {
        AssetSnapshot snapshot = AssetChanges.Take(assets.Snapshot, state);
        snapshot = AssetReindex.Take(snapshot, state, assets);
        snapshot = AssetLoads.Land(snapshot, state);
        assets.Snapshot = AssetLoads.Start(snapshot, state, assets.Stopped);
        state.TellWaiters();

        return ValueTask.FromResult(state);
    }
}
