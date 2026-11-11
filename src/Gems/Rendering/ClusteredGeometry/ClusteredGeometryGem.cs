using System.Collections.Immutable;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Ecs;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.ClusteredGeometry;

/// <summary>
/// What the cluster renderer takes each frame: the world as the GPU holds it.
/// </summary>
public readonly record struct ClusteredGeometryInput(GpuView View);

/// <summary>
/// Draws every model the world's view holds as a hierarchy of clusters, GPU-driven, and far away as its impostor: each
/// frame its clusters culled, drawn into a visibility buffer and resolved by material into the view's GBuffer and glass
/// targets, with its depth. As a model loads, its preprocessor clusters it once per change to its file and bakes its
/// impostor. As a domain loads, it holds the loading screen until the models it draws are clustered on the GPU and
/// their resolve passes are built. It is public because nothing in its assembly makes it: Magic does, by reflection,
/// when a project lists the gem.
/// </summary>
public sealed class ClusteredGeometryGem
    : IGem,
        IHasSettings<ClusteredGeometrySettings>,
        IRenderFrom<ClusteredGeometryInput>,
        IHookIntoDomainLoading,
        IDisposable
{
    private readonly Threads _threads;
    private readonly Subscription _preprocessing;
    private readonly ClusterResources _resources;
    private readonly Visibility _visibility;

    /// <summary>
    /// Whether the gem draws: failed once a shader is not built. Render thread.
    /// </summary>
    private Result _started = Result.Success();

    /// <summary>
    /// How many domains have loaded since the start, and what the newest waits on before it shows: its models clustered
    /// on the GPU and their resolve passes built, once the view counts it prepared. Render thread.
    /// </summary>
    private int _domainsLoaded;
    private TaskCompletionSource? _loading;

    public ClusteredGeometryGem(ClusteredGeometrySettings settings, AssetManager assets, Threads threads)
    {
        Settings = settings;
        _threads = threads;
        ClusterPreprocessor clusters = new();
        _preprocessing = assets.Register(clusters);
        _resources = new ClusterResources(clusters, assets, threads);
        _visibility = new Visibility(assets);
    }

    /// <summary>
    /// When a model draws as its impostor. The game thread sets it whole, and <see cref="Render"/> reads the record it
    /// finds, which never changes.
    /// </summary>
    public ClusteredGeometrySettings Settings { get; set; }

    public void Dispose()
    {
        _preprocessing.Dispose();
        _loading?.TrySetResult();
        _loading = null;
    }

    /// <summary>
    /// Keeps the models the view holds clustered on the GPU, and records the frame's passes in the geometry stage.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in ClusteredGeometryInput input)
    {
        if (!_started.Ok)
            return;

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails ends the start.
        GpuState shaders = graph.GetState(_visibility.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            FailStart("a shader was not built.");

        if (shaders != GpuState.Ready)
            return;

        ClusteredGeometrySettings settings = Settings;
        GpuView gpu = input.View;
        Result<ClusterPools> pools = _resources.Prepare(graph, gpu.Models.Span);
        if (!pools.Ok)
        {
            FailStart(pools.Error);

            return;
        }

        // The loading domain shows once its models are clustered on the GPU and the resolve for each material shader is
        // built.
        if (
            _loading is { } loading
            && gpu.PreparedDomains >= _domainsLoaded
            && !_resources.IsLoading
            && graph.GetState(_visibility.GetResolveShaders(gpu.MaterialShaders)) != GpuState.Loading
        )
        {
            loading.TrySetResult();
            _loading = null;
        }

        Result<RenderView> view = Visibility.Prepare(
            graph,
            gpu,
            pools.Value,
            settings.ImpostorPixels,
            settings.ImpostorDistance
        );
        if (!view.Ok)
        {
            Log.Error($"The clusters are not drawn: {view.Error}");

            return;
        }

        _visibility.Record(view.Value);
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
        _loading?.TrySetResult();
        _loading = null;
        if (!_started.Ok)
            return;

        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = loading;
        await loading.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the start as failed: the gem draws nothing, and holds no domain back.
    /// </summary>
    private void FailStart(string error)
    {
        Log.Error($"The clusters are not drawn: {error}");
        _loading?.TrySetResult();
        _loading = null;
        _started = Result.Failure(error);
    }
}
