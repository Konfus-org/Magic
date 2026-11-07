using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Files;
using Magic.Input;
using Magic.Projects;
using Magic.Rendering;
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
    private readonly AssetManager _assets;
    private readonly IEcs _ecs;
    private readonly App _app;
    private readonly EventCoordinator _events;
    private readonly Threads _threads;
    private readonly Mailbox<RenderInputs> _inputs = new();
    private readonly Channel<ScreenshotRequested> _screenshots = Channel.CreateUnbounded<ScreenshotRequested>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly GpuResources _resources;
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
    private readonly ShadowMask? _shadowMask;
    private readonly Lighting _lighting;
    private readonly SurfaceView _surfaceView;
    private readonly RendererDebugWindow? _debugWindow;

    /// <summary>
    /// Every shader the areas' passes run, which the GPU provider builds before the frame is drawn.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shaders;

    /// <summary>
    /// Ends the start when the gem is disposed before it is done, so nothing is made for a gem that is gone.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();
    private readonly Subscription _preprocessing;

    private DebugView _debugView;

    /// <summary>
    /// The inputs <see cref="Render"/> draws, taken from <see cref="_inputs"/>, until newer are. Render thread.
    /// </summary>
    private RenderInputs? _current;
    private bool _hasDrawn;

    /// <summary>
    /// How the start ended: null until the pools are made, then whether they were, and failed once a shader does.
    /// Render thread.
    /// </summary>
    private Result? _started;

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until <see cref="Render"/> has uploaded it all. Render
    /// thread.
    /// </summary>
    private Preparing? _preparing;

    public GIRenderingGem(
        IGraphicsApi gpu,
        AssetManager assets,
        Caches caches,
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
        ModelPreprocessor models = new();
        _preprocessing = assets.Register(models);
        _resources = new GpuResources(gpu, models, caches, assets, threads);
        _failure = new MaterialInstance { Handle = assets.GetFailure<Material, Model>() };
        _instances = new InstanceTable(gpu, assets, _resources, _failure);
        _rayTracing = gpu.Features.HasRayQuery ? new RayTracing() : null;
        _visibility = new Visibility(assets, _resources);
        _frameView = new FrameView(_resources);
        _sky = new SkyLuts(assets);
        _lightTiles = new LightTiles(assets);
        _shadowMask = _rayTracing is null ? null : new ShadowMask(assets, _resources);
        _lighting = new Lighting(assets, _resources);
        _surfaceView = new SurfaceView(assets);
        _shaders =
        [
            .. _visibility.Shaders,
            .. _sky.Shaders,
            .. _lightTiles.Shaders,
            .. _shadowMask?.Shaders ?? [],
            .. _lighting.Shaders,
            .. _surfaceView.Shaders,
        ];
        StartAsync().FireAndForget();
    }

    public void Dispose()
    {
        _preprocessing.Dispose();
        _stopping.Cancel();
        _stopping.Dispose();
        EndPreparing();
        _resources.Dispose();
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

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails ends the start.
        GpuState shaders = GetState(graph, _shaders.AsSpan());
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

        // Entities -> resources -> rows: the renderers written since the inputs before ask for their models and have
        // their rows built again, the materials' rows are made again at upload, and a model no renderer drawn holds
        // leaves once nothing asked for it in a while.
        int instanceCount = _instances.Fill(inputs.Renderers, frame.Index);
        _resources.EvictUnusedModels(inputs.Renderers.Second[..instanceCount]);

        // The loading domain shows once what it draws is on the GPU.
        if (_preparing is { } preparing && IsPrepared(preparing))
        {
            Log.Info(
                $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
            );
            EndPreparing();
        }

        graph.BeginTransferPass("Upload arrivals");
        _resources.Upload(graph);
        graph.EndPass();

        // The frame's tables, and the view's targets at the window's size.
        RenderCamera camera = GetCamera(inputs.Cameras);
        RenderSun sun = GetSun(inputs.DirectionalLights);
        graph.BeginTransferPass("Upload frame");
        Result<UploadedFrame> uploaded = UploadFrame(graph, inputs, camera, sun, frame.Index, instanceCount);
        graph.EndPass();
        if (!uploaded.Ok)
        {
            Log.Error($"The frame is not drawn: {uploaded.Error}");

            return;
        }

        // The passes, in the order they run, each reading what the ones before it wrote: what the view sees, the sky's
        // tables while there is a sky, each tile's lights, each pixel's shadows on a GPU that traces rays, the shading,
        // and the output.
        (RenderView view, SkyTables sky, LightBuffers lights, ShadowBuffers? shadows, GpuBuffer lighting) =
            uploaded.Value;
        GpuConstants header = new(view.Constants.Id, 0, lighting.Id);
        _visibility.Record(view);
        if (inputs.Skies.Count > 0)
            _sky.Record(graph, header, view.Constants, lighting, sky);

        _lightTiles.Record(view, header, lighting, lights);
        if (_shadowMask is not null && shadows is { } shadowed)
            _shadowMask.Record(view, header, lighting, lights, shadowed);

        _lighting.Record(view, header, lighting, sky, lights, shadows);
        _surfaceView.Record(view, inputs.DebugView, sun.Exposure);

        GpuTexture target = _gpu.WindowTarget;
        if (!target.IsNone)
        {
            graph.BeginTransferPass("Show");
            graph.Use(view.Output, GpuAccess.CopySource);
            graph.Use(target, GpuAccess.CopyDestination);
            graph.BlitTexture(view.Output, target);
            graph.EndPass();
        }

        // One readback buffer, so one screenshot a frame: the last asked for.
        ScreenshotRequested? shot = null;
        while (_screenshots.Reader.TryRead(out ScreenshotRequested asked))
        {
            shot = asked;
        }

        if (shot is { } saved)
            SaveScreenshot(graph, view, saved.Path);

        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }
    }

    /// <summary>
    /// Forgets what was drawn before, last frame's depth, camera and shadows, so nothing of it shows in what comes.
    /// </summary>
    public async Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        await _threads.Enter(EngineThread.Render);
        _frameView.ClearHistory();
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
            _resources.Get(material);
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
            _resources.Get(model);
            isPrepared &= _resources.HasArrived(model);
            hasFailure |= _resources.HasFailed(model);
        }

        foreach (Handle<Material> material in preparing.Materials)
        {
            isPrepared &= _resources.Get(material) != ResourceState.Loading;
        }

        if (!hasFailure || _failure.Handle == Handle<Material>.None)
            return isPrepared;

        return _resources.Get(_failure.Handle) != ResourceState.Loading && isPrepared;
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
    /// Makes the pools on the render thread, whose the GPU is: a run that ends before that draws nothing.
    /// </summary>
    private async Task StartAsync()
    {
        CancellationToken stopping = _stopping.Token;
        await _threads.Enter(EngineThread.Render);
        stopping.ThrowIfCancellationRequested();
        Result made = _resources.Create();
        if (!made.Ok)
        {
            FailStart(made.Error);

            return;
        }

        _started = made;
    }

    /// <summary>
    /// Ends the start as failed: the renderer draws nothing, and holds no domain back.
    /// </summary>
    private void FailStart(string error)
    {
        Log.Error($"The renderer draws nothing: {error}");
        EndPreparing();
        _started = Result.Failure(error);
    }

    /// <summary>
    /// The frame's tables and the view's constants uploaded, each asking for what it uploads into, in a transfer pass the
    /// caller opened; what the passes read and write, or what was not made, and the frame is not drawn.
    /// </summary>
    private Result<UploadedFrame> UploadFrame(
        RenderGraph graph,
        RenderInputs inputs,
        RenderCamera camera,
        RenderSun sun,
        uint frameIndex,
        int instanceCount
    )
    {
        // The world's structure is built again only when the rows changed, before their upload marks them unchanged; a
        // model's arrival builds every row again, so it builds the structure too.
        if (_rayTracing is not null && _instances.HasChanges)
        {
            Result built = _rayTracing.Build(graph, _instances.Rows, _instances.Extras);
            if (!built.Ok)
                return Result.Failure(built.Error);
        }

        Result<(GpuBuffer Rows, GpuBuffer Materials)> tables = _instances.Upload(graph);
        if (!tables.Ok)
            return Result.Failure(tables.Error);

        // The cull's buffers before the view's constants, which name them.
        Result<CullBuffers> cull = Visibility.Prepare(graph);
        if (!cull.Ok)
            return Result.Failure(cull.Error);

        Result<RenderView> view = _frameView.Upload(
            graph,
            camera,
            sun,
            frameIndex,
            inputs.Width,
            inputs.Height,
            instanceCount,
            tables.Value.Rows,
            tables.Value.Materials,
            cull.Value,
            _rayTracing?.Acceleration ?? GpuAcceleration.None
        );
        if (!view.Ok)
            return Result.Failure(view.Error);

        Result<LightBuffers> lights = _lightTiles.Prepare(
            view.Value,
            inputs.PointLights,
            inputs.SpotLights,
            inputs.RectLights
        );
        if (!lights.Ok)
            return Result.Failure(lights.Error);

        // The sky's tables and the shadow mask before the lighting constants, which name them.
        Result<SkyTables> sky = SkyLuts.Prepare(graph);
        if (!sky.Ok)
            return Result.Failure(sky.Error);

        Result<ShadowBuffers>? shadows = _shadowMask?.Prepare(view.Value);
        if (shadows is { Ok: false } failed)
            return Result.Failure(failed.Error);

        Result<GpuBuffer> lighting = _lighting.Prepare(
            view.Value,
            inputs.Skies,
            camera,
            sun,
            sky.Value,
            lights.Value,
            shadows?.Value
        );
        if (!lighting.Ok)
            return Result.Failure(lighting.Error);

        return Result.Success(new UploadedFrame(view.Value, sky.Value, lights.Value, shadows?.Value, lighting.Value));
    }

    /// <summary>
    /// The view's output read back, and written to <paramref name="path"/> once the GPU is done with it.
    /// </summary>
    private void SaveScreenshot(RenderGraph graph, in RenderView view, string path)
    {
        Result<GpuBuffer> readback = graph.Buffer(
            GpuDescriptions.GetBuffer("Output readback", (long)view.Width * view.Height * 4, BufferUsages.Readback)
        );
        if (!readback.Ok)
        {
            Log.Error($"The screenshot was not taken: {readback.Error}");

            return;
        }

        Screenshot.RecordReadback(graph, view.Output, readback.Value);
        Screenshot.SaveAsync(_gpu, _assets, readback.Value, path, view.Width, view.Height).FireAndForget();
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
    /// What the frame's upload hands its passes: the view, the sky's tables, the lights, the shadow mask on a GPU that
    /// traces rays, and the lighting constants that name them.
    /// </summary>
    private readonly record struct UploadedFrame(
        RenderView View,
        SkyTables Sky,
        LightBuffers Lights,
        ShadowBuffers? Shadows,
        GpuBuffer Lighting
    );

    /// <summary>
    /// The models and materials a domain loading draws, and what ends once they are all on the GPU.
    /// </summary>
    private sealed record Preparing(
        ImmutableArray<Handle<Model>> Models,
        ImmutableArray<Handle<Material>> Materials,
        TaskCompletionSource Done
    );
}
