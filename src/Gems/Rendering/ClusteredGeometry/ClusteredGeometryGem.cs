using System.Collections.Immutable;
using System.Globalization;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Files;
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
        IRenderFrom<ClusteredGeometryInput>,
        IHookIntoDomainLoading,
        IDisposable
{
    private readonly Threads _threads;
    private readonly Mailbox<RenderInputs> _inputs = new();
    private readonly Subscription _preprocessing;
    private readonly ClusterResources _resources;
    private readonly Visibility _visibility;

    /// <summary>
    /// The pixel radius on screen under which an instance hands off to its impostor: half an atlas view of 64 texels,
    /// so an impostor is never magnified. Set with ClusteredGeometry.impostorPixels; 0 hands off by distance alone.
    /// </summary>
    private float _impostorPixels = 32;

    /// <summary>
    /// The distance in metres to an instance past which it hands off to its impostor, however large on screen. Set with
    /// ClusteredGeometry.impostorDistance; 0 hands off by size alone.
    /// </summary>
    private float _impostorDistance = 1000;

    /// <summary>
    /// The settings <see cref="Render"/> draws with, taken from <see cref="_inputs"/>, until newer are. Render thread.
    /// </summary>
    private RenderInputs? _current;

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

    public ClusteredGeometryGem(AssetManager assets, Caches caches, Threads threads)
    {
        _threads = threads;
        ClusterPreprocessor clusters = new();
        _preprocessing = assets.Register(clusters);
        _resources = new ClusterResources(clusters, caches, assets, threads);
        _visibility = new Visibility(assets);
    }

    public void Dispose()
    {
        _preprocessing.Dispose();
        _loading?.TrySetResult();
        _loading = null;
    }

    /// <summary>
    /// Applies the settings.
    /// </summary>
    public void Update(in Frame frame)
    {
        foreach (SetCliCommand setting in frame.Events.Get<SetCliCommand>())
        {
            ApplySetting(setting);
        }
    }

    /// <summary>
    /// Publishes the settings for <see cref="Render"/>.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        RenderInputs back = _inputs.Back;
        back.ImpostorPixels = _impostorPixels;
        back.ImpostorDistance = _impostorDistance;
        _inputs.Publish();
    }

    /// <summary>
    /// Keeps the models the view holds clustered on the GPU, and records the frame's passes in the geometry stage.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in ClusteredGeometryInput input)
    {
        if (!_started.Ok)
            return;

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails ends the start.
        GpuState shaders = GetState(graph, _visibility.Shaders.AsSpan());
        if (shaders == GpuState.Failed)
            FailStart("a shader was not built.");

        if (shaders != GpuState.Ready)
            return;

        if (_inputs.TryTake(out RenderInputs? newest))
        {
            if (_current is not null)
                _inputs.Return(_current);

            _current = newest;
        }

        if (_current is not { } inputs)
            return;

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
            && GetState(graph, _visibility.GetResolveShaders(gpu.MaterialShaders)) != GpuState.Loading
        )
        {
            loading.TrySetResult();
            _loading = null;
        }

        Result<RenderView> view = Visibility.Prepare(
            graph,
            gpu,
            pools.Value,
            inputs.ImpostorPixels,
            inputs.ImpostorDistance
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
    /// Where <paramref name="shaders"/> are: failed if any failed, loading while any loads, else ready. Each is asked,
    /// so the first ask starts them all.
    /// </summary>
    private static GpuState GetState(RenderGraph graph, ReadOnlySpan<ShaderEntry> shaders)
    {
        GpuState all = GpuState.Ready;
        foreach (ShaderEntry shader in shaders)
        {
            GpuState state = graph.GetState(shader);
            if (state == GpuState.Failed)
                all = GpuState.Failed;
            else if (state == GpuState.Loading && all == GpuState.Ready)
                all = GpuState.Loading;
        }

        return all;
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

    private void ApplySetting(in SetCliCommand setting)
    {
        if (setting.Key == "ClusteredGeometry.impostorPixels")
        {
            if (float.TryParse(setting.Value, CultureInfo.InvariantCulture, out float pixels) && pixels >= 0)
                _impostorPixels = pixels;
            else
                Log.Error($"{setting.Key} takes a pixel radius of 0 or more, not '{setting.Value}'.");

            return;
        }

        if (setting.Key != "ClusteredGeometry.impostorDistance")
            return;

        if (float.TryParse(setting.Value, CultureInfo.InvariantCulture, out float metres) && metres >= 0)
            _impostorDistance = metres;
        else
            Log.Error($"{setting.Key} takes a distance in metres of 0 or more, not '{setting.Value}'.");
    }

    /// <summary>
    /// What the game thread's settings ask of a frame, copied as its frame ends and published for the render thread,
    /// which reads nothing else of the game thread's.
    /// </summary>
    private sealed class RenderInputs
    {
        /// <summary>
        /// The pixel radius on screen under which an instance hands off to its impostor; 0 hands off by distance alone.
        /// </summary>
        public float ImpostorPixels { get; set; }

        /// <summary>
        /// The distance in metres to an instance past which it hands off to its impostor; 0 hands off by size alone.
        /// </summary>
        public float ImpostorDistance { get; set; }
    }
}
