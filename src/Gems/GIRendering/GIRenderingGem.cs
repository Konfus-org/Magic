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

    /// <summary>
    /// The shading a model that failed is drawn with, every slot; null when there is no failure material.
    /// </summary>
    private readonly ShadingInstance? _failure;
    private readonly Visibility _visibility;
    private readonly FrameView _frameView;
    private readonly SkyLuts _sky;
    private readonly LightTiles _lightTiles;
    private readonly ShadowMask? _shadows;
    private readonly Lighting _lighting;
    private readonly SurfaceShading _surfaceShading;
    private readonly SurfaceView _surfaceView;
    private readonly IOverlay? _overlay;
    private readonly RendererDebugWindow? _debugWindow;
    private DebugView _debugView;
    private bool _isStarting;
    private bool _isReady;
    private bool _hasFailedToStart;
    private bool _hasHzb;
    private bool _hasDrawn;

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until <see cref="Render"/> has uploaded it all. Main thread.
    /// </summary>
    private Preparing? _preparing;

    /// <summary>
    /// What the passes compiled into, written whole by the compile on its worker and taken by <see cref="Render"/>.
    /// </summary>
    private CompiledPasses? _compiled;

    public GIRenderingGem(
        IGraphicsApi gpu,
        IShaderCompiler shaders,
        Assets assets,
        IMeshPreprocessor meshes,
        IEcs ecs,
        World world,
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
        _instances = new InstanceTable(gpu, _graph, world);
        Handle<Material> failure = assets.GetFailure<Material, Model>();
        _failure = failure == Handle<Material>.None ? null : new ShadingInstance(failure);
        _rayTracing = gpu.Features.HasRayQuery ? new RayTracing(gpu, _graph) : null;
        _visibility = new Visibility(gpu, _graph, _pipelines, _pools, _materials);
        _frameView = new FrameView(gpu, _graph, _pools, _materials, _visibility, _rayTracing);
        _sky = new SkyLuts(gpu, _graph, _pipelines);
        _lightTiles = new LightTiles(gpu, _graph, _pipelines);
        _shadows = _rayTracing is null
            ? null
            : new ShadowMask(gpu, _graph, _pipelines, _pools, _visibility, _lightTiles);
        _lighting = new Lighting(gpu, _graph, _sky, _lightTiles, _shadows);
        _surfaceShading = new SurfaceShading(_pipelines, _lighting);
        _surfaceView = new SurfaceView(_pipelines);
    }

    public void Dispose()
    {
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
    /// Reads what the frame draws out of the entities, and starts compiling the passes on the first frame.
    /// </summary>
    public void Update(in Frame frame)
    {
        StartCompiling();
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
        if (!_isReady && Interlocked.Exchange(ref _compiled, null) is { } compiled)
            Start(compiled.Result);

        if (!_isReady)
            return;

        int width = _app.Window is { Width: > 0, Height: > 0 } window ? window.Width : DefaultWidth;
        int height = _app.Window is { Width: > 0, Height: > 0 } sized ? sized.Height : DefaultHeight;
        if (!Resize(width, height))
            return;

        // What arrived: models into the pools, textures, material rows.
        if (_preparing is { } asking)
            AskFor(asking);

        _materials.Refresh();
        _graph.BeginTransferPass("Upload arrivals");
        _pools.Upload();
        _textures.Upload();
        _materials.Upload();
        _graph.EndPass();
        if (_preparing is { } preparing && HasArrived(preparing))
        {
            Log.Info(
                $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
            );
            EndPreparing();
        }

        // The frame's tables.
        RenderCamera camera = _world.Camera ?? new RenderCamera(new Transform(), 60, 0.05f);
        RenderSun sun = _world.Sun ?? new RenderSun(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One, 1);
        GpuBuffer constants = _frameView.Constants;
        int instanceCount = _instances.Fill(frame.Index, _pools, _materials, _failure);
        _graph.BeginTransferPass("Upload frame");
        bool hasChanges = _pools.HasArrivals || _instances.HasChanges;
        _instances.Upload();
        _rayTracing?.Prepare(_instances.Rows, _instances.Extras, hasChanges);
        _frameView.Upload(
            constants,
            _frameView.GetMain(camera, sun, frame.Index, _targets, _instances.Buffer, instanceCount, _hasHzb)
        );
        _visibility.ResetCounts();
        _lighting.Prepare(_world, camera, sun, _targets);
        _graph.EndPass();

        // The passes, in the order they run.
        RenderView view = new(constants, _instances.Buffer, instanceCount, _targets);
        _lighting.RecordTables(constants);
        _visibility.RecordMain(view);
        _visibility.RecordResolve(view);
        _lighting.RecordTiles(view);
        _lighting.RecordShadows(view);
        _surfaceShading.Record(view);
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
        _hasHzb = true;
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
        _hasHzb = false;
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
        if (_hasFailedToStart)
            return Task.CompletedTask;

        HashSet<Handle<Model>> models = [];
        HashSet<Handle<Material>> materials = [];
        foreach (ImmutableArray<IComponent> entity in entities)
        {
            AddWhatDraws(entity, models, materials);
        }

        StartCompiling();
        foreach (Handle<Material> material in materials)
        {
            _materials.Load(material);
        }

        EndPreparing();
        _preparing = new Preparing(
            [.. models],
            [.. materials],
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );

        return _preparing.Done.Task.WaitAsync(cancel);
    }

    /// <summary>
    /// The model and the materials of <paramref name="entity"/>'s renderer, when it has one that draws.
    /// </summary>
    private static void AddWhatDraws(
        ImmutableArray<IComponent> entity,
        HashSet<Handle<Model>> models,
        HashSet<Handle<Material>> materials
    )
    {
        foreach (IComponent component in entity)
        {
            if (component is not Renderer renderer || renderer.Model == Handle<Model>.None)
                continue;

            models.Add(renderer.Model);
            materials.UnionWith(renderer.Materials.All.Where(material => material != Handle<Material>.None));
        }
    }

    /// <summary>
    /// Starts compiling the passes, once.
    /// </summary>
    private void StartCompiling()
    {
        if (_isStarting)
            return;

        _isStarting = true;
        CompileAsync().FireAndForget();
    }

    /// <summary>
    /// Asks for the models <paramref name="preparing"/> waits for, which the pools only take once they are made, and for
    /// the failure material a model that failed is drawn with. Each frame while it waits, before the uploads; an ask for
    /// one asked for already does nothing.
    /// </summary>
    private void AskFor(Preparing preparing)
    {
        bool hasFailure = false;
        foreach (Handle<Model> model in preparing.Models)
        {
            _pools.Get(model);
            hasFailure |= _pools.HasFailed(model);
        }

        if (hasFailure && _failure is { } failure)
            _materials.Load(failure.Material);
    }

    /// <summary>
    /// Whether every model and material <paramref name="preparing"/> waits for is on the GPU, or failed and drawn as the
    /// failure, which is on the GPU too.
    /// </summary>
    private bool HasArrived(Preparing preparing)
    {
        bool hasFailure = false;
        foreach (Handle<Model> model in preparing.Models)
        {
            if (!_pools.HasArrived(model))
                return false;

            hasFailure |= _pools.HasFailed(model);
        }

        foreach (Handle<Material> material in preparing.Materials)
        {
            if (!_materials.HasArrived(material))
                return false;
        }

        return !hasFailure || _failure is not { } failure || _materials.HasArrived(failure.Material);
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
    /// Compiles the passes on a worker, for <see cref="Render"/> to make the pipelines from.
    /// </summary>
    private async Task CompileAsync()
    {
        Result<ImmutableArray<CompiledShader>> compiled = await _pipelines
            .CompileAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Interlocked.Exchange(ref _compiled, new CompiledPasses(compiled));
    }

    /// <summary>
    /// Makes the pipelines, the pools and the tables from what the passes compiled into, in the frame, once: a run that
    /// ends before that draws nothing, and finds a shader that does not compile only if it runs long enough to.
    /// </summary>
    private void Start(Result<ImmutableArray<CompiledShader>> compiled)
    {
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
            _hasFailedToStart = true;
            EndPreparing();

            return;
        }

        _isReady = true;
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

        _hasHzb = false;

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
    /// What the passes compiled into, boxed so it is written whole from the compile's worker.
    /// </summary>
    private sealed class CompiledPasses(Result<ImmutableArray<CompiledShader>> result)
    {
        public Result<ImmutableArray<CompiledShader>> Result { get; } = result;
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
