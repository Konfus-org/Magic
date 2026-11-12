using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.ClusteredGeometry;

/// <summary>
/// What the cluster renderer takes each frame: the world as the GPU holds it, or null in a frame without a view.
/// </summary>
public readonly record struct ClusteredGeometryInput(GpuView? View);

/// <summary>
/// Draws every model the world's view holds as a hierarchy of clusters, GPU-driven, and far away as its impostor: each
/// frame its clusters culled, drawn into a visibility buffer and resolved by material into the view's GBuffer and glass
/// targets, with its depth. As a model loads, its preprocessor clusters it once per change to its file and bakes its
/// impostor. As a domain loads, it holds the loading screen until the models it draws are clustered on the GPU and
/// their resolve passes are built.
/// </summary>
public sealed class Renderer : IRenderFrom<ClusteredGeometryInput>, IHookIntoDomainLoading, IDisposable
{
    /// <summary>
    /// When a model draws as its impostor. The game thread writes it, and <see cref="Render"/> reads it.
    /// </summary>
    private readonly ClusteredGeometrySettings _settings;
    private readonly Threads _threads;
    private readonly Subscription _preprocessing;
    private readonly ClusterResources _resources;
    private readonly ClusterPasses _passes;

    /// <summary>
    /// How many domains have loaded since the start, and what the newest waits on before it shows: its models clustered
    /// on the GPU and their resolve passes built, once the view counts it prepared. Render thread.
    /// </summary>
    private int _domainsLoaded;
    private TaskCompletionSource? _loading;

    public Renderer(ClusteredGeometrySettings settings, AssetManager assets, Threads threads)
    {
        _settings = settings;
        _threads = threads;
        ClusterPreprocessor clusters = new();
        _preprocessing = assets.Register(clusters);
        _resources = new ClusterResources(clusters, assets, threads);
        _passes = new ClusterPasses(assets);
    }

    public void Dispose()
    {
        _preprocessing.Dispose();
        EndLoading();
    }

    /// <summary>
    /// Keeps the models the view holds clustered on the GPU, and records the frame's passes in the geometry stage.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in ClusteredGeometryInput input)
    {
        // A frame without a view because its maker is out lets the loading domain show: the failure is already told.
        // Otherwise the view is still loading, and the domain keeps waiting for the clusters.
        if (input.View is not { } gpu)
        {
            if (graph.IsMakerOut<GpuView>())
                EndLoading();

            return;
        }

        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState shaders = graph.GetState(_passes.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            Fail(graph, RenderFailure.Shader, "a shader of the clusters was not built.");

        if (shaders != GpuState.Ready)
            return;

        Result<ClusterPools> pools = _resources.Prepare(graph, gpu.Models.Span);
        if (!pools.Ok)
        {
            Fail(graph, RenderFailure.Resource, pools.Error);

            return;
        }

        // The loading domain shows once its models are clustered on the GPU and the resolve for each material shader is
        // built.
        if (
            _loading is not null
            && gpu.PreparedDomains >= _domainsLoaded
            && !_resources.IsLoading
            && graph.GetState(_passes.GetResolveShaders(gpu.MaterialShaders)) != GpuState.Loading
        )
        {
            EndLoading();
        }

        Result recorded = _passes.Record(graph, gpu, pools.Value, _settings);
        if (!recorded.Ok)
            Fail(graph, RenderFailure.Resource, recorded.Error);
    }

    /// <summary>
    /// Holds the domain loading until its models are clustered on the GPU and their resolve passes are built, once the
    /// world's view counts what it draws on the GPU. One that comes while another waits ends that one. On the render
    /// thread, whose count this is.
    /// </summary>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        _domainsLoaded++;
        EndLoading();
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = loading;
        await loading.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the graph that the clusters are not drawn in this frame, and lets the loading domain show without them.
    /// </summary>
    private void Fail(RenderGraph graph, RenderFailure kind, string reason)
    {
        graph.Fail(kind, "Clusters are not drawn", reason);
        EndLoading();
    }

    /// <summary>
    /// Lets the loading domain show, when one waits.
    /// </summary>
    private void EndLoading()
    {
        _loading?.TrySetResult();
        _loading = null;
    }
}
