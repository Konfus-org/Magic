using System.Collections.Immutable;
using System.Linq;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.ClusteredGeometry;

/// <summary>
/// What the cluster renderer takes once a frame: the world as the GPU holds it, or null in a frame without one.
/// </summary>
public readonly record struct ClusterPoolsInput(GpuWorld? World);

/// <summary>
/// What the cluster renderer takes for each view of the frame: the view as the GPU holds it, or null where the view
/// was not made, and the pools the frame's models are clustered in.
/// </summary>
public readonly record struct ClusteredGeometryInput(GpuView? View, ClusterPools Pools);

/// <summary>
/// Draws every model that the world holds as a hierarchy of clusters, GPU-driven, and far away as its impostor.
/// </summary>
/// <remarks>
/// Once a frame it keeps the models clustered on the GPU, in one set of pools for every view. For each view it culls
/// the clusters, draws them into a visibility buffer, and resolves them by material into the view's GBuffer and glass
/// targets, with their depth. As a model loads, its preprocessor clusters it once per change to its file and bakes its
/// impostor. As a domain loads, it holds the loading screen until the models it draws are clustered on the GPU and
/// their resolve passes are built.
/// </remarks>
public sealed class Renderer
    : Disposable,
        IRender<ClusterPoolsInput, ClusterPools>,
        IRenderFrom<ClusteredGeometryInput>,
        IHookIntoDomainLoading
{
    /// <summary>
    /// When a model draws as its impostor. The game thread writes it, and the step of a view reads it.
    /// </summary>
    private readonly ClusteredGeometrySettings _settings;
    private readonly Threads _threads;
    private readonly IGraphicsApi _gpu;
    private readonly Subscription _preprocessing;
    private readonly ClusterResources _resources;
    private readonly ClusterPasses _passes;

    /// <summary>
    /// How many domains have loaded since the start. Render thread.
    /// </summary>
    /// <remarks>
    /// The newest waits until its models are clustered on the GPU and their resolve passes are built, once the world
    /// counts it prepared.
    /// </remarks>
    private int _domainsLoaded;
    private TaskCompletionSource? _loading;

    internal Renderer(ClusteredGeometrySettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        _settings = settings;
        _threads = threads;
        _gpu = gpu;
        Track(EndLoading);
        ClusterPreprocessor clusters = new();
        _preprocessing = assets.Register(clusters);
        Track(_preprocessing);
        _resources = new ClusterResources(clusters, assets, threads, gpu);
        _passes = new ClusterPasses(assets, gpu);
    }

    /// <summary>
    /// Keeps the models the world holds clustered on the GPU, once a frame for all its views, and makes the pools they
    /// are in; none while the shaders build or after the pools were not made.
    /// </summary>
    public ClusterPools? Render(in RenderFrame frame, RenderGraph graph, in ClusterPoolsInput input)
    {
        // A frame without a world because its maker is out lets the loading domain show: the failure is already told.
        // Otherwise the world is still loading, and the domain keeps waiting for the clusters.
        if (input.World is not { } world)
        {
            if (frame.IsUnavailable<GpuWorld>())
                EndLoading();

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState shaders = _gpu.GetState(_passes.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            Fail(frame, RenderFailure.Shader, "a shader of the clusters was not built.");

        if (shaders != GpuState.Ready)
            return null;

        Result<ClusterPools> pools = _resources.Prepare(graph, world.Models.Span);
        if (!pools.Ok)
        {
            Fail(frame, RenderFailure.Resource, pools.Error);

            return null;
        }

        // The loading domain shows once its models are clustered on the GPU and the resolve for each material shader is
        // built.
        if (
            _loading is not null
            && world.PreparedDomains >= _domainsLoaded
            && !_resources.IsLoading
            && _gpu.GetState(_passes.GetResolveShaders(world.MaterialShaders)) != GpuState.Loading
        )
        {
            EndLoading();
        }

        return pools.Value;
    }

    /// <summary>
    /// Records the passes of the view of this turn in the geometry stage, over the pools the frame made.
    /// </summary>
    public void Render(in RenderFrame frame, RenderGraph graph, in ClusteredGeometryInput input)
    {
        // A frame without a view because its maker is out lets the loading domain show: the failure is already told.
        if (input.View is not { } gpu)
        {
            if (frame.IsUnavailable<GpuView>())
                EndLoading();

            return;
        }

        Result recorded = _passes.Record(frame, graph, gpu, input.Pools, _settings);
        if (!recorded.Ok)
            Fail(frame, RenderFailure.Resource, recorded.Error);
    }

    /// <summary>
    /// Starts to read the clusters of each model that <paramref name="entities"/> draw, so that they load while the
    /// world fades to the loading screen, before the view's model table holds the models.
    /// </summary>
    /// <remarks>
    /// The entities are gone through on a worker, so no frame of the fade waits for them. The reads start on the render
    /// thread, whose rows these are.
    /// </remarks>
    public async Task OnDomainReadAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel)
    {
        ImmutableArray<Handle<Model>> models = await Task.Run(
                () =>
                    entities
                        .SelectMany(entity => entity.OfType<Ecs.Components.Renderer>())
                        .Select(renderer => renderer.Model)
                        .Where(model => model != Handle<Model>.None)
                        .Distinct()
                        .ToImmutableArray(),
                cancel
            )
            .ConfigureAwait(false);
        await _threads.Enter(EngineThread.Render);
        _resources.Start(models);
    }

    /// <summary>
    /// Holds the domain loading until its models are clustered on the GPU and their resolve passes are built. Render
    /// thread.
    /// </summary>
    /// <remarks>
    /// It waits until the world counts what it draws on the GPU. A loading that comes while another waits ends that
    /// one.
    /// </remarks>
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
    /// Tells the frame that the clusters are not drawn in this frame, and lets the loading domain show without them.
    /// </summary>
    private void Fail(in RenderFrame frame, RenderFailure kind, string reason)
    {
        frame.Fail(kind, "Clusters are not drawn", reason);
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
