using System.Collections.Immutable;
using System.Linq;
using Magic.Assets;
using Magic.DeferredRendering;
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
/// What the cluster renderer takes to draw the sun's shadow maps of a view: the view, the pools the frame's models are
/// clustered in, and the view's cascades.
/// </summary>
public readonly record struct CascadeDepthInput(GpuView View, ClusterPools Pools, CascadeViews? Cascades);

/// <summary>
/// What the cluster renderer takes to draw the local lights' shadow maps, once a frame: the world, the pools the
/// frame's models are clustered in, and the lights' tiles.
/// </summary>
public readonly record struct LocalDepthInput(GpuWorld World, ClusterPools Pools, LocalShadowViews? Views);

/// <summary>
/// What the cluster renderer takes to draw the sky's direction maps, once a frame: the world, the pools the frame's
/// models are clustered in, and the maps' draws.
/// </summary>
public readonly record struct SkyDepthInput(GpuWorld World, ClusterPools Pools, SkyViews? Views);

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
        IRender<CascadeDepthInput, CascadeDepth>,
        IRender<LocalDepthInput, LocalShadowDepth>,
        IRender<SkyDepthInput, SkyDepth>,
        IHookIntoDomainLoading
{
    private static readonly ShadowPassNames _cascadeNames = new("Cascade", "cascade");

    private static readonly ShadowPassNames _cacheNames = new("Shadow cache", "shadow cache");

    private static readonly ShadowPassNames _localNames = new("Local shadow", "local shadow");

    private static readonly ShadowPassNames _nearSkyNames = new("Near sky map", "near sky map");

    private static readonly ShadowPassNames _farSkyNames = new("Far sky map", "far sky map");

    /// <summary>
    /// When a model draws as its impostor. The game thread writes it, and the step of a view reads it.
    /// </summary>
    private readonly ClusteredGeometrySettings _settings;
    private readonly Threads _threads;
    private readonly IGraphicsApi _gpu;
    private readonly Subscription _preprocessing;
    private readonly ClusterResources _resources;
    private readonly ClusterPasses _passes;
    private readonly ShadowPasses _shadowPasses;

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
        _shadowPasses = new ShadowPasses(assets, gpu);
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

        // The world's buffer holds the pools and the impostor handoff of the settings, for what is culled once a frame
        // from the world's main camera.
        ClusterConstants worldClusters = new()
        {
            ImpostorPixels = _settings.ImpostorPixels,
            ImpostorDistance = _settings.ImpostorDistance,
            Clusters = pools.Value.Clusters.Id,
            Indices = pools.Value.Indices.Id,
            Vertices = pools.Value.Vertices.Id,
            Models = pools.Value.Models.Id,
            Impostors = pools.Value.Impostors.Id,
        };
        if (!graph.Upload(world.Frame, [worldClusters], ClusterConstants.Offset))
        {
            Fail(frame, RenderFailure.Resource, "the world's cluster constants were not staged.");

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
    /// Draws the depth of each cascade of the view of this turn into the view's cascade atlas, in the geometry stage;
    /// none while the shadow shaders build or after what they draw into was not made.
    /// </summary>
    public CascadeDepth? Render(in RenderFrame frame, RenderGraph graph, in CascadeDepthInput input)
    {
        // Without the gem that fits these views, nothing is drawn.
        if (input.Cascades is not { } cascades)
            return null;

        GpuState shaders = _gpu.GetState(_shadowPasses.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "Shadows are not drawn", "a shader of the shadow maps was not built.");

        if (shaders != GpuState.Ready)
            return null;

        GpuView view = input.View;
        Result<GpuTexture> atlas = _gpu.GetTexture(
            new(
                "Cascade atlas",
                cascades.AtlasSize,
                cascades.AtlasSize,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            ),
            frame.View
        );
        Result drawn = atlas.Ok
            ? _shadowPasses.Record(
                graph,
                new ShadowTarget
                {
                    Names = _cascadeNames,
                    Frame = view.Frame,
                    View = frame.View,
                    Views = cascades.Views,
                    ViewCount = cascades.Count,
                    Instances = view.Instances,
                    InstanceCount = view.InstanceCount,
                    Materials = view.Materials,
                    Vertices = view.Vertices,
                    Atlas = atlas.Value,
                    IsCleared = true,
                },
                input.Pools
            )
            : Result.Failure(atlas.Error);
        if (drawn.Ok)
            return new CascadeDepth(atlas.Value);

        frame.Fail(RenderFailure.Resource, "Shadows are not drawn", drawn.Error);

        return null;
    }

    /// <summary>
    /// Draws the still casters of the local lights' tiles that the cache draws again into the cache, then each tile's
    /// casters into the local atlas over the copies of the cache, once a frame in the geometry stage; none while the
    /// shadow shaders build or after what they draw into was not made.
    /// </summary>
    public LocalShadowDepth? Render(in RenderFrame frame, RenderGraph graph, in LocalDepthInput input)
    {
        // Without the gem that fits these views, nothing is drawn.
        if (input.Views is not { } views)
            return null;

        GpuState shaders = _gpu.GetState(_shadowPasses.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "Shadows are not drawn", "a shader of the shadow maps was not built.");

        if (shaders != GpuState.Ready)
            return null;

        GpuWorld world = input.World;
        ShadowTarget cache = new()
        {
            Names = _cacheNames,
            Frame = world.Frame,
            View = null,
            Views = views.CacheViews,
            ViewCount = views.CacheCount,
            Instances = world.Instances,
            InstanceCount = world.InstanceCount,
            Materials = world.Materials,
            Vertices = world.Vertices,
            Atlas = views.Cache,
            IsCleared = false,
        };
        Result drawn = views.CacheCount > 0 ? _shadowPasses.Record(graph, cache, input.Pools) : Result.Success();
        if (drawn.Ok && views.AtlasCount > 0)
        {
            drawn = _shadowPasses.Record(
                graph,
                cache with
                {
                    Names = _localNames,
                    Views = views.AtlasViews,
                    ViewCount = views.AtlasCount,
                    Atlas = views.Atlas,
                },
                input.Pools
            );
        }

        if (drawn.Ok)
            return new LocalShadowDepth(views.Atlas);

        frame.Fail(RenderFailure.Resource, "Shadows are not drawn", drawn.Error);

        return null;
    }

    /// <summary>
    /// Draws the still casters of this frame's draws of the sky's direction maps into their atlases, once a frame in the
    /// geometry stage; none while the shadow shaders build or after what they draw into was not made.
    /// </summary>
    public SkyDepth? Render(in RenderFrame frame, RenderGraph graph, in SkyDepthInput input)
    {
        // Without the gem that fits these views, nothing is drawn.
        if (input.Views is not { } views)
            return null;

        GpuState shaders = _gpu.GetState(_shadowPasses.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            frame.Fail(
                RenderFailure.Shader,
                "The sky's maps are not drawn",
                "a shader of the shadow maps was not built."
            );

        if (shaders != GpuState.Ready)
            return null;

        GpuWorld world = input.World;
        ShadowTarget near = new()
        {
            Names = _nearSkyNames,
            Frame = world.Frame,
            View = null,
            Views = views.NearDraws,
            ViewCount = views.NearDrawCount,
            Instances = world.Instances,
            InstanceCount = world.InstanceCount,
            Materials = world.Materials,
            Vertices = world.Vertices,
            Atlas = views.NearAtlas,
            IsCleared = false,
        };
        Result drawn = views.NearDrawCount > 0 ? _shadowPasses.Record(graph, near, input.Pools) : Result.Success();
        if (drawn.Ok && views.FarDrawCount > 0)
        {
            drawn = _shadowPasses.Record(
                graph,
                near with
                {
                    Names = _farSkyNames,
                    Views = views.FarDraws,
                    ViewCount = views.FarDrawCount,
                    Atlas = views.FarAtlas,
                },
                input.Pools
            );
        }

        if (drawn.Ok)
            return new SkyDepth(views.NearAtlas, views.FarAtlas);

        frame.Fail(RenderFailure.Resource, "The sky's maps are not drawn", drawn.Error);

        return null;
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
