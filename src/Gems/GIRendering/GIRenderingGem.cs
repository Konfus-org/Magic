using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
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
/// Draws the world. <c>LateUpdate</c> copies what the frame draws out of the entities and publishes it; <c>Render</c>, on
/// the render thread, takes the newest published, records the frame in order, top to bottom, and shows it. As a domain
/// replaces the world, it forgets what it drew before and holds the loading screen until what comes is on the GPU. It is
/// public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GIRenderingGem : IGem, IHookIntoDomainLoading, IDisposable
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;

    /// <summary>
    /// What a surface facing the sun lands at in the picture before the tonemap, with no meter yet.
    /// </summary>
    private const float ExposureAtSun = 4;

    private readonly IGraphicsApi _gpu;
    private readonly Assets _assets;
    private readonly IEcs _ecs;
    private readonly App _app;
    private readonly EventCoordinator _events;
    private readonly Threads _threads;
    private readonly Mailbox<RenderInputs> _inputs = new();
    private readonly Channel<ScreenshotRequested> _screenshots = Channel.CreateUnbounded<ScreenshotRequested>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly GpuPipelines _pipelines;
    private readonly GeometryPools _pools;
    private readonly TextureTable _textures;
    private readonly MaterialTable _materials;
    private readonly FrameTargets _targets;
    private readonly RayTracing? _rayTracing;
    private readonly InstanceTable _instances;

    /// <summary>
    /// The material a model that failed is drawn with, every slot; an empty slot when there is no failure material.
    /// </summary>
    private readonly MaterialInstance _failure;
    private readonly Visibility _visibility;
    private readonly FrameView _frameView;
    private readonly SkyLuts _sky;
    private readonly LightTiles _lightTiles;
    private readonly ShadowMask? _shadows;
    private readonly Lighting _lighting;
    private readonly SurfaceView _surfaceView;
    private readonly RendererDebugWindow? _debugWindow;

    /// <summary>
    /// Ends the start when the gem is disposed before it is done, so nothing is made for a gem that is gone.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    private DebugView _debugView;

    /// <summary>
    /// The inputs <see cref="Render"/> draws, taken from <see cref="_inputs"/>, until newer are. Render thread.
    /// </summary>
    private RenderInputs? _current;
    private bool _hasDrawn;

    /// <summary>
    /// How the start ended: null while the passes compile, then whether the pipelines, the pools and the tables were
    /// made. Render thread.
    /// </summary>
    private Result? _started;

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until <see cref="Render"/> has uploaded it all. Render
    /// thread.
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
        IInput? input = null
    )
    {
        _debugWindow = debugUI is null ? null : new RendererDebugWindow(debugUI, input);
        _gpu = gpu;
        _assets = assets;
        _ecs = ecs;
        _app = app;
        _events = events;
        _threads = threads;
        _pipelines = new GpuPipelines(gpu, shaders, assets);
        _pools = new GeometryPools(gpu, meshes, assets, threads);
        _textures = new TextureTable(gpu, assets, threads);
        _materials = new MaterialTable(gpu, assets, _textures, threads);
        _targets = new FrameTargets(gpu);
        _instances = new InstanceTable(gpu);
        _failure = new MaterialInstance { Handle = assets.GetFailure<Material, Model>() };
        _rayTracing = gpu.Features.HasRayQuery ? new RayTracing(gpu) : null;
        _visibility = new Visibility(gpu, _pipelines, _pools, _materials);
        _frameView = new FrameView(gpu, _pools, _materials, _visibility, _rayTracing);
        _sky = new SkyLuts(gpu, _pipelines);
        _lightTiles = new LightTiles(gpu, _pipelines);
        _shadows = _rayTracing is null ? null : new ShadowMask(gpu, _pipelines, _pools, _visibility, _lightTiles);
        _lighting = new Lighting(gpu, _pipelines, _sky, _lightTiles, _shadows);
        _surfaceView = new SurfaceView(_pipelines);
        StartAsync().FireAndForget();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        EndPreparing();
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
    /// Applies the settings, and the debug window's.
    /// </summary>
    public void Update(in Frame frame)
    {
        foreach (SetCliCommand setting in frame.Events.Get<SetCliCommand>())
        {
            ApplySetting(setting);
        }

        if (_debugWindow is not null)
            _debugView = _debugWindow.Update(_debugView);
    }

    /// <summary>
    /// Copies what the frame draws out of the entities and publishes it for <see cref="Render"/>, with the window's size
    /// and the debug view, after the screenshots asked for.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        RenderInputs back = _inputs.Back;
        _ecs.Snapshot(back.Renderers);
        _ecs.Snapshot(back.Cameras);
        _ecs.Snapshot(back.DirectionalLights);
        _ecs.Snapshot(back.PointLights);
        _ecs.Snapshot(back.SpotLights);
        _ecs.Snapshot(back.RectLights);
        _ecs.Snapshot(back.Skies);
        back.DebugView = _debugView;
        (back.Width, back.Height) = _app.Window is { Width: > 0, Height: > 0 } window
            ? (window.Width, window.Height)
            : (DefaultWidth, DefaultHeight);
        foreach (ScreenshotRequested asked in frame.Events.Get<ScreenshotRequested>())
        {
            _screenshots.Writer.TryWrite(asked);
        }

        _inputs.Publish();
    }

    /// <summary>
    /// Records the newest inputs published, or the last again when none are newer, into the frame's graph.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph)
    {
        if (_started is not { Ok: true })
            return;

        if (_inputs.TryTake(out RenderInputs? newest))
        {
            if (_current is not null)
                _inputs.Return(_current);

            _current = newest;
        }

        if (_current is not { } inputs)
            return;

        // The rows of the renderers written since the inputs before.
        int instanceCount = _instances.Fill(inputs.Renderers, frame.Index, _pools, _materials, _failure);

        if (!Resize(inputs.Width, inputs.Height))
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

        graph.BeginTransferPass("Upload arrivals");
        _pools.Upload(graph);
        _textures.Upload(graph);
        _materials.Upload(graph);
        graph.EndPass();

        // The frame's tables.
        RenderCamera camera = GetCamera(inputs.Cameras);
        RenderSun sun = GetSun(inputs.DirectionalLights);
        graph.BeginTransferPass("Upload frame");
        bool hasChanges = _pools.HasArrivals || _instances.HasChanges;
        _instances.Upload(graph);
        _rayTracing?.Prepare(graph, _instances.Rows, _instances.Extras, hasChanges);
        _frameView.Upload(graph, camera, sun, frame.Index, _targets, _instances.Buffer, instanceCount);
        _visibility.ResetCounts(graph);
        _lightTiles.Prepare(graph, inputs.PointLights, inputs.SpotLights, inputs.RectLights);
        _lighting.Prepare(graph, inputs.Skies, camera, sun, _targets);
        graph.EndPass();

        // The passes, in the order they run.
        RenderView view = new(_frameView.Constants, _instances.Buffer, instanceCount, _targets, graph);
        _visibility.Record(view);
        _lighting.Record(view);
        _surfaceView.Record(view, inputs.DebugView, sun.Exposure);

        GpuTexture target = _gpu.WindowTarget;
        if (!target.IsNone)
        {
            graph.BeginTransferPass("Show");
            graph.Use(_targets.Output, GpuAccess.CopySource);
            graph.Use(target, GpuAccess.CopyDestination);
            graph.BlitTexture(_targets.Output, target);
            graph.EndPass();
        }

        // One readback buffer, so one screenshot a frame: the last asked for.
        ScreenshotRequested? shot = null;
        while (_screenshots.Reader.TryRead(out ScreenshotRequested asked))
        {
            shot = asked;
        }

        if (shot is not null)
            Screenshot.RecordReadback(graph, _targets);

        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }

        if (shot is { } saved)
        {
            Screenshot
                .SaveAsync(_gpu, _assets, _targets.Readback, saved.Path, inputs.Width, inputs.Height)
                .FireAndForget();
        }
    }

    /// <summary>
    /// Forgets what was drawn before, last frame's depth, camera and shadows, so nothing of it shows in what comes.
    /// </summary>
    public async Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        await _threads.Enter(EngineThread.Render);
        _frameView.ClearHistory();
        _shadows?.ClearHistory();
    }

    /// <summary>
    /// Asks for every model and material <paramref name="entities"/> draw, and ends in the frame they are on the GPU, or
    /// failed and drawn as the failure, with the passes made. Materials are asked for at once; models once the pools are
    /// made, by <see cref="Render"/>. One that comes while another waits ends that one. On the render thread, whose
    /// tables these are.
    /// </summary>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        if (_started is { Ok: false })
            return;

        Renderer[] renderers =
        [
            .. entities
                .SelectMany(entity => entity.OfType<Renderer>())
                .Where(renderer => renderer.Model != Handle<Model>.None),
        ];
        ImmutableArray<Handle<Material>> materials =
        [
            .. renderers
                .SelectMany(renderer => renderer.Materials.All.Select(slot => slot.Handle))
                .Where(material => material != Handle<Material>.None)
                .Distinct(),
        ];
        foreach (Handle<Material> material in materials)
        {
            _materials.Load(material);
        }

        EndPreparing();
        Preparing preparing = new(
            [.. renderers.Select(renderer => renderer.Model).Distinct()],
            materials,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );
        _preparing = preparing;
        await preparing.Done.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The first camera that draws into the window, else one at the origin.
    /// </summary>
    private static RenderCamera GetCamera(EcsSnapshot<Transform, Camera> cameras)
    {
        for (int i = 0; i < cameras.Count; i++)
        {
            Camera camera = cameras.Second[i];
            if (camera.Target.IsWindow)
                return new RenderCamera(cameras.First[i], camera.FieldOfView, camera.Near);
        }

        return new RenderCamera(new Transform(), 60, 0.05f);
    }

    /// <summary>
    /// The directional light the frame is lit by: the sun while it is up, else the moon while it is up, else the first
    /// there is, else a white light from above.
    /// </summary>
    private static RenderSun GetSun(EcsSnapshot<Transform, DirectionalLight> lights)
    {
        RenderSun sun = new(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One, 1);
        int sunRank = -1;
        for (int i = 0; i < lights.Count; i++)
        {
            DirectionalLight light = lights.Second[i];
            Vector3 towards = -Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, lights.First[i].Rotation));
            bool isUp = towards.Y > 0;
            int rank = 0;
            if (isUp && light.Mode == DirectionalLightMode.Sun)
                rank = 2;
            else if (isUp && light.Mode == DirectionalLightMode.Moon)
                rank = 1;

            if (rank <= sunRank)
                continue;

            Vector3 illuminance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity;
            float exposure = light.Intensity > 0 ? ExposureAtSun / light.Intensity : 1;
            sun = new RenderSun(towards, illuminance, exposure);
            sunRank = rank;
        }

        return sun;
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

        if (!hasFailure || _failure.Handle == Handle<Material>.None)
            return isPrepared;

        _materials.Load(_failure.Handle);

        return isPrepared && _materials.HasArrived(_failure.Handle);
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
    /// Compiles the passes on a worker, then makes the pipelines, the pools and the tables from them on the render
    /// thread, whose the GPU is: a run that ends before that draws nothing, and finds a shader that does not compile only
    /// if it runs long enough to.
    /// </summary>
    private async Task StartAsync()
    {
        CancellationToken stopping = _stopping.Token;
        Result<ImmutableArray<CompiledShader>> compiled = await _pipelines.CompileAsync(stopping).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Render);
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
