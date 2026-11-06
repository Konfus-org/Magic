using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Input;
using Magic.Projects;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.GIRendering;

/// <summary>
/// Draws the world. <c>Update</c> reads what the frame draws out of the entities; <c>Render</c> records the frame in
/// order, top to bottom, and shows it. As a domain replaces the world, it forgets what it drew before and holds the
/// loading screen until what comes is on the GPU. It is public because nothing in its assembly makes it: Magic does, by
/// reflection, when a project lists the gem.
/// </summary>
public sealed class GIRenderingGem : IGem, IHookIntoDomainLoading, IDisposable
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;

    private readonly IGraphicsApi _gpu;
    private readonly Assets _assets;
    private readonly IEcs _ecs;
    private readonly App _app;
    private readonly EventCoordinator _events;
    private readonly RenderGraph _graph;
    private readonly RenderWorld _world = new();
    private readonly GpuPipelines _pipelines;
    private readonly GeometryPools _pools;
    private readonly TextureTable _textures;
    private readonly MaterialTable _materials;
    private readonly FrameTargets _targets;
    private readonly RayTracing? _rayTracing;
    private readonly InstanceTable _instances;
    private readonly Visibility _visibility;
    private readonly FrameView _frameView;
    private readonly SkyLuts _sky;
    private readonly LightTiles _lightTiles;
    private readonly ShadowMask? _shadows;
    private readonly Lighting _lighting;
    private readonly SurfaceView _surfaceView;
    private readonly IOverlay? _overlay;
    private readonly RendererDebugWindow? _debugWindow;

    /// <summary>
    /// Ends the start when the gem is disposed before it is done, so nothing is made for a gem that is gone.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    private DebugView _debugView;
    private bool _hasDrawn;

    /// <summary>
    /// How the start ended: null while the passes compile, then whether the pipelines, the pools and the tables were
    /// made. Main thread.
    /// </summary>
    private Result? _started;

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until <see cref="Render"/> has uploaded it all. Main thread.
    /// </summary>
    private Preparing? _preparing;

    public GIRenderingGem(
        IGraphicsApi gpu,
        IShaderCompiler shaders,
        Assets assets,
        IMeshPreprocessor meshes,
        IEcs ecs,
        Threads threads,
        App app,
        EventCoordinator events,
        IDebugUI? debugUI = null,
        IOverlay? overlay = null,
        IInput? input = null
    )
    {
        _overlay = overlay;
        _debugWindow = debugUI is null ? null : new RendererDebugWindow(debugUI, input);
        _gpu = gpu;
        _assets = assets;
        _ecs = ecs;
        _app = app;
        _events = events;
        _graph = new RenderGraph(gpu);
        _pipelines = new GpuPipelines(gpu, _graph, shaders, assets);
        _pools = new GeometryPools(gpu, _graph, meshes, assets, threads);
        _textures = new TextureTable(gpu, _graph, assets, threads);
        _materials = new MaterialTable(gpu, _graph, assets, _textures, threads);
        _targets = new FrameTargets(gpu, _graph);
        _instances = new InstanceTable(gpu, _graph);
        _rayTracing = gpu.Features.HasRayQuery ? new RayTracing(gpu, _graph) : null;
        _visibility = new Visibility(gpu, _graph, _pipelines, _pools, _materials);
        _frameView = new FrameView(gpu, _graph, _pools, _materials, _visibility, _rayTracing);
        _sky = new SkyLuts(gpu, _pipelines);
        _lightTiles = new LightTiles(gpu, _graph, _pipelines);
        _shadows = _rayTracing is null
            ? null
            : new ShadowMask(gpu, _graph, _pipelines, _pools, _visibility, _lightTiles);
        _lighting = new Lighting(gpu, _graph, _pipelines, _sky, _lightTiles, _shadows);
        _surfaceView = new SurfaceView(_pipelines);
        StartAsync(threads).FireAndForget();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        EndPreparing();
        _graph.Clear();
        _pipelines.Dispose();
        _targets.Dispose();
        _materials.Dispose();
        _textures.Dispose();
        _pools.Dispose();
        _instances.Dispose();
        _rayTracing?.Dispose();
        _visibility.Dispose();
        _frameView.Dispose();
        _sky.Dispose();
        _lightTiles.Dispose();
        _shadows?.Dispose();
        _lighting.Dispose();
    }

    /// <summary>
    /// Reads what the frame draws out of the entities.
    /// </summary>
    public void Update(in Frame frame)
    {
        WorldCopy.Read(_ecs, _world);
        _ecs.Snapshot(_instances.Renderers);
        foreach (SetCliCommand setting in frame.Events.Get<SetCliCommand>())
        {
            ApplySetting(setting);
        }

        if (_debugWindow is not null)
            _debugView = _debugWindow.Update(_debugView);
    }

    /// <summary>
    /// Draws the frame and submits it.
    /// </summary>
    public void Render(in Frame frame)
    {
        if (_started is not { Ok: true })
            return;

        int width = _app.Window is { Width: > 0, Height: > 0 } window ? window.Width : DefaultWidth;
        int height = _app.Window is { Width: > 0, Height: > 0 } sized ? sized.Height : DefaultHeight;
        if (!Resize(width, height))
            return;

        // What arrived: models into the pools, textures, material rows.
        _materials.Refresh();
        if (_preparing is { } preparing && IsPrepared(preparing))
        {
            Log.Info(
                $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
            );
            EndPreparing();
        }

        _graph.BeginTransferPass("Upload arrivals");
        _pools.Upload();
        _textures.Upload();
        _materials.Upload();
        _graph.EndPass();

        // The frame's tables.
        RenderCamera camera = _world.Camera ?? new RenderCamera(new Transform(), 60, 0.05f);
        RenderSun sun = _world.Sun ?? new RenderSun(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One, 1);
        int instanceCount = _instances.Fill(frame.Index, _pools, _materials, _assets.GetFailure<Material, Model>());
        _graph.BeginTransferPass("Upload frame");
        bool hasChanges = _pools.HasArrivals || _instances.HasChanges;
        _instances.Upload();
        _rayTracing?.Prepare(_instances.Rows, _instances.Extras, hasChanges);
        _frameView.Upload(camera, sun, frame.Index, _targets, _instances.Buffer, instanceCount);
        _visibility.ResetCounts();
        _lighting.Prepare(_world, camera, sun, _targets);
        _graph.EndPass();

        // The passes, in the order they run.
        RenderView view = new(_frameView.Constants, _instances.Buffer, instanceCount, _targets);
        _visibility.Record(view);
        _lighting.Record(view);
        _surfaceView.Record(view, _debugView, sun.Exposure);
        _overlay?.Record(_graph, _targets.Output);

        GpuTexture target = _gpu.WindowTarget;
        if (!target.IsNone)
        {
            _graph.Import(target, GpuAccess.None);
            _graph.BeginTransferPass("Show");
            _graph.Use(_targets.Output, GpuAccess.CopySource);
            _graph.Use(target, GpuAccess.CopyDestination);
            _graph.Recorder.BlitTexture(_targets.Output, target);
            _graph.EndPass();
        }

        // One readback buffer, so one screenshot a frame: the last asked for.
        ReadOnlySpan<ScreenshotRequested> requests = frame.Events.Get<ScreenshotRequested>();
        if (requests.Length > 0)
            Screenshot.RecordReadback(_graph, _targets);

        _gpu.Submit(_graph.Finish());
        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }

        if (requests.Length > 0)
            Screenshot.SaveAsync(_gpu, _assets, _targets.Readback, requests[^1].Path, width, height).FireAndForget();
    }

    /// <summary>
    /// Forgets what was drawn before, last frame's depth, camera and shadows, so nothing of it shows in what comes.
    /// </summary>
    public Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        _frameView.ClearHistory();
        _shadows?.ClearHistory();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks for every model and material <paramref name="entities"/> draw, and ends in the frame they are on the GPU, or
    /// failed and drawn as the failure, with the passes made. Materials are asked for at once; models once the pools are
    /// made, by <see cref="Render"/>. One that comes while another waits ends that one.
    /// </summary>
    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel)
    {
        if (_started is { Ok: false })
            return Task.CompletedTask;

        Renderer[] renderers =
        [
            .. entities
                .SelectMany(entity => entity.OfType<Renderer>())
                .Where(renderer => renderer.Model != Handle<Model>.None),
        ];
        ImmutableArray<Handle<Material>> materials =
        [
            .. renderers
                .SelectMany(renderer => renderer.Materials.All)
                .Where(material => material != Handle<Material>.None)
                .Distinct(),
        ];
        foreach (Handle<Material> material in materials)
        {
            _materials.GetRow(material);
        }

        EndPreparing();
        _preparing = new Preparing(
            [.. renderers.Select(renderer => renderer.Model).Distinct()],
            materials,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );

        return _preparing.Done.Task.WaitAsync(cancel);
    }

    /// <summary>
    /// Asks for the models <paramref name="preparing"/> waits for, which the pools only take once they are made, and for
    /// the failure material a model that failed is drawn with, an ask for one asked for already doing nothing; then
    /// whether every one of them and every material is on the GPU, or failed and drawn as the failure, which is on the
    /// GPU too.
    /// </summary>
    private bool IsPrepared(Preparing preparing)
    {
        bool isPrepared = true;
        bool hasFailure = false;
        foreach (Handle<Model> model in preparing.Models)
        {
            _pools.Get(model);
            isPrepared &= _pools.HasArrived(model);
            hasFailure |= _pools.HasFailed(model);
        }

        foreach (Handle<Material> material in preparing.Materials)
        {
            isPrepared &= _materials.HasArrived(material);
        }

        Handle<Material> failure = _assets.GetFailure<Material, Model>();
        if (!hasFailure || failure == Handle<Material>.None)
            return isPrepared;

        _materials.GetRow(failure);

        return isPrepared && _materials.HasArrived(failure);
    }

    /// <summary>
    /// Lets the domain waiting on what it draws show.
    /// </summary>
    private void EndPreparing()
    {
        _preparing?.Done.TrySetResult();
        _preparing = null;
    }

    /// <summary>
    /// Compiles the passes on a worker, then makes the pipelines, the pools and the tables from them on the main thread:
    /// a run that ends before that draws nothing, and finds a shader that does not compile only if it runs long enough
    /// to.
    /// </summary>
    private async Task StartAsync(Threads threads)
    {
        CancellationToken stopping = _stopping.Token;
        Result<ImmutableArray<CompiledShader>> compiled = await _pipelines.CompileAsync(stopping).ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        stopping.ThrowIfCancellationRequested();
        Result made = compiled.Ok ? _pipelines.Create(compiled.Value) : Result.Failure(compiled.Error);
        foreach (
            Func<Result> step in (ReadOnlySpan<Func<Result>>)
                [
                    _pools.Create,
                    _materials.Create,
                    _instances.Create,
                    () => _rayTracing?.Create() ?? Result.Success(),
                    _visibility.Create,
                    _frameView.Create,
                    _sky.Create,
                    _lightTiles.Create,
                    _lighting.Create,
                ]
        )
        {
            if (made.Ok)
                made = step();
        }

        // A renderer that draws nothing holds no domain back.
        if (!made.Ok)
        {
            Log.Error($"The renderer draws nothing: {made.Error}");
            EndPreparing();
        }

        _started = made;
    }

    /// <summary>
    /// Whether the targets fit this frame's size, made again when they don't; false when that failed, and the frame is
    /// not drawn.
    /// </summary>
    private bool Resize(int width, int height)
    {
        if (_targets.Width == width && _targets.Height == height)
            return true;

        Result resized = _targets.Resize(width, height);
        if (resized.Ok)
            resized = _lightTiles.Resize(width, height);

        if (resized.Ok && _shadows is not null)
            resized = _shadows.Resize(width, height);

        if (!resized.Ok)
        {
            Log.Error($"The frame is not drawn: {resized.Error}");

            return false;
        }

        _frameView.ClearHistory();

        return true;
    }

    private void ApplySetting(in SetCliCommand setting)
    {
        if (setting.Key == "GIRendering.window")
        {
            if (_debugWindow is not null && bool.TryParse(setting.Value, out bool isOpen))
                _debugWindow.IsOpen = isOpen;
            else
                Log.Error($"{setting.Key} takes true or false, with a debug UI to draw it, not '{setting.Value}'.");

            return;
        }

        if (setting.Key != "GIRendering.debug")
            return;

        // A number parses as any value of the enum, named or not.
        if (Enum.TryParse(setting.Value, ignoreCase: true, out DebugView view) && Enum.IsDefined(view))
            _debugView = view;
        else
            Log.Error($"{setting.Key} takes {string.Join(", ", Enum.GetNames<DebugView>())}, not '{setting.Value}'.");
    }

    /// <summary>
    /// The models and materials a domain loading draws, and what ends once they are all on the GPU.
    /// </summary>
    private sealed record Preparing(
        ImmutableArray<Handle<Model>> Models,
        ImmutableArray<Handle<Material>> Materials,
        TaskCompletionSource Done
    );
}
