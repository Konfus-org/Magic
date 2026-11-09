using System.Collections.Immutable;
using System.Globalization;
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

    /// <summary>
    /// What is done to the picture when no entity has a <see cref="PostProcessing"/>: the temporal anti-aliasing, the
    /// exposure and the AgX tonemap.
    /// </summary>
    private readonly PostProcessing _defaultPost = new();
    private readonly Visibility _visibility;
    private readonly FrameView _frameView;
    private readonly SkyLuts _sky;
    private readonly LightTiles _lightTiles;
    private readonly ShadowMask? _shadowMask;
    private readonly Probes? _probes;
    private readonly TracedDiffuse? _tracedDiffuse;
    private readonly TracedReflections? _reflections;
    private readonly Lighting _lighting;
    private readonly PixelEffects _pixelEffects;
    private readonly SurfaceView _surfaceView;
    private readonly RendererDebugWindow? _debugWindow;

    /// <summary>
    /// Every shader the areas' passes run, which the GPU provider builds before the frame is drawn.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shaders;
    private readonly Subscription _preprocessing;

    private DebugView _debugView;

    /// <summary>
    /// The pixel radius on screen under which an instance hands off to its impostor: half an atlas view, so an impostor
    /// is never magnified. Set with GIRendering.impostorPixels; 0 hands off by distance alone.
    /// </summary>
    private float _impostorPixels = MeshDerived.ImpostorTile / 2;

    /// <summary>
    /// The distance in metres to an instance past which it hands off to its impostor, however large on screen. Set with
    /// GIRendering.impostorDistance; 0 hands off by size alone.
    /// </summary>
    private float _impostorDistance = 1000;

    /// <summary>
    /// The number of the world the frames <see cref="LateUpdate"/> publishes are of, a new one each time a domain
    /// replaces it, so <see cref="Render"/> forgets what it drew before at the first frame of what comes. Game thread.
    /// </summary>
    private int _worldNumber;

    /// <summary>
    /// The inputs <see cref="Render"/> draws, taken from <see cref="_inputs"/>, until newer are. Render thread.
    /// </summary>
    private RenderInputs? _current;
    private bool _hasDrawn;

    /// <summary>
    /// Whether the renderer draws: failed once a shader or a pool is not made. Render thread.
    /// </summary>
    private Result _started = Result.Success();

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until <see cref="Render"/> has uploaded it all. Render
    /// thread.
    /// </summary>
    private Preparing? _preparing;

    public GIRenderingGem(
        IGraphicsApi gpu,
        IShaderCompiler shaders,
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
        _resources = new GpuResources(models, caches, assets, shaders, threads);
        _failure = new MaterialInstance { Handle = assets.GetFailure<Material, Model>() };
        _defaultPost.Effects[0] = new MaterialInstance
        {
            Handle = assets.Find<Material>("Materials/TemporalAntiAliasing.mat"),
        };
        _defaultPost.Effects[1] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Exposure.mat") };
        _defaultPost.Effects[2] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Agx.mat") };
        _instances = new InstanceTable(assets, _resources, _failure);
        _rayTracing = gpu.Features.HasRayQuery ? new RayTracing(assets, _resources) : null;
        _visibility = new Visibility(assets, _resources);
        _frameView = new FrameView(_resources);
        _sky = new SkyLuts(assets);
        _lightTiles = new LightTiles(assets);
        _shadowMask = _rayTracing is null ? null : new ShadowMask(assets, _resources);
        _probes = _rayTracing is null ? null : new Probes(assets, _resources);
        _tracedDiffuse = _rayTracing is null ? null : new TracedDiffuse(assets, _resources);
        _reflections = _rayTracing is null ? null : new TracedReflections(assets, _resources);
        _lighting = new Lighting(assets, _resources);
        _pixelEffects = new PixelEffects(assets, _resources);
        _surfaceView = new SurfaceView(assets);
        _shaders =
        [
            .. _visibility.Shaders,
            .. _sky.Shaders,
            .. _lightTiles.Shaders,
            .. _shadowMask?.Shaders ?? [],
            .. _probes?.Shaders ?? [],
            .. _tracedDiffuse?.Shaders ?? [],
            .. _reflections?.Shaders ?? [],
            .. _lighting.Shaders,
            .. _surfaceView.Shaders,
        ];
    }

    public void Dispose()
    {
        _preprocessing.Dispose();
        EndPreparing();
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
        _ecs.Snapshot(back.PostProcessing);
        _ecs.Snapshot(back.Weather);
        back.WorldNumber = _worldNumber;
        back.DebugView = _debugView;
        back.ImpostorPixels = _impostorPixels;
        back.ImpostorDistance = _impostorDistance;
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
        if (!_started.Ok)
            return;

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails ends the start.
        GpuState shaders = GetState(graph, _shaders.AsSpan());
        if (shaders == GpuState.Failed)
            FailStart("a shader was not built.");

        if (shaders != GpuState.Ready)
            return;

        // The pools before any model is asked for, whose acceleration structure names them, and every frame, so what
        // they hold stays.
        Result pools = _resources.Prepare(graph);
        if (!pools.Ok)
        {
            FailStart(pools.Error);

            return;
        }

        // The models made since the last frame settle into the pools before any renderer's row is built.
        _resources.SettleModels(graph);

        if (_inputs.TryTake(out RenderInputs? newest))
        {
            // The first frame of another world forgets last frame's depth, camera and shadows and the probes' light, so
            // nothing drawn before shows in it, and its effects take their first reading of it at once.
            if (newest.WorldNumber != _current?.WorldNumber)
            {
                _frameView.ClearHistory();
                _probes?.ClearHistory();
            }

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

        // The loading domain shows once what it draws and its effects are on the GPU, and the resolve for each material
        // shader is built.
        if (
            _preparing is { } preparing
            && IsPrepared(graph, preparing)
            && GetState(graph, _visibility.GetResolveShaders()) != GpuState.Loading
        )
        {
            Log.Info(
                $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
            );
            EndPreparing();
        }

        _resources.Upload(graph);

        // The air's effects before the picture's, so a post effect works on the fogged picture; the camera jitters while
        // one of them asks for it.
        MaterialSlots air = inputs.Weather.Count > 0 ? inputs.Weather.First[0].Effects : default;
        PostProcessing post = inputs.PostProcessing.Count > 0 ? inputs.PostProcessing.First[0] : _defaultPost;
        ReadOnlySpan<MaterialSlots> effects = [air, post.Effects];
        Vector2 jitter = _pixelEffects.HasJitter(graph, effects) ? FrameView.GetJitter(frame.Index) : Vector2.Zero;

        // The frame's tables, and the view's targets at the window's size.
        RenderCamera camera = GetCamera(inputs.Cameras);
        RenderSun sun = GetSun(inputs.DirectionalLights);
        Result<UploadedFrame> uploaded = UploadFrame(graph, inputs, camera, sun, frame, jitter, instanceCount);
        if (!uploaded.Ok)
        {
            Log.Error($"The frame is not drawn: {uploaded.Error}");

            return;
        }

        // The passes, in the order they run, each reading what the ones before it wrote: what the view sees, the sky's
        // tables while there is a sky, each tile's lights, on a GPU that traces rays the probes' light, the traced
        // diffuse light, the traced reflections and each pixel's shadows, the shading, a debug view's light or probes in
        // its place or over it, the first weather's effects and the first post processing's or the default's, which
        // finish the picture, and the output.
        (
            RenderView view,
            SkyTables sky,
            LightBuffers lights,
            ShadowBuffers? shadows,
            TracedWorld? traced,
            ProbeBuffers? probes,
            TracedDiffuseBuffers? diffuse,
            TracedReflectionBuffers? reflections
        ) = uploaded.Value;
        GpuConstants header = new(view.Constants.Id);
        _visibility.Record(view);
        if (inputs.Skies.Count > 0)
            _sky.Record(graph, header, view.Constants, sky);

        _lightTiles.Record(view, header, lights);
        if (_rayTracing is not null && _probes is not null && traced is { } world && probes is { } probed)
        {
            _rayTracing.RecordHitMaterials(view, world.HitMaterials, _instances.MaterialCount);
            _probes.Record(view, world.HitMaterials, sky, lights, probed);
            if (_tracedDiffuse is not null && diffuse is { } bounced)
                _tracedDiffuse.Record(view, world.HitMaterials, sky, lights, probed, bounced);
            if (_reflections is not null && reflections is { } reflected)
                _reflections.Record(view, world.HitMaterials, sky, lights, probed, reflected);
        }

        if (_shadowMask is not null && shadows is { } shadowed && traced is { } shadowWorld)
            _shadowMask.Record(view, header, lights, shadowed, shadowWorld.HitMaterials);

        _lighting.Record(view, header, sky, lights, shadows, diffuse, reflections, probes, traced);

        // A debug view of light draws it into the picture, so the effects finish it as they do the picture.
        _surfaceView.RecordLight(view, inputs.DebugView, diffuse, reflections?.Light ?? GpuTexture.None);
        if (_probes is not null && inputs.DebugView == DebugView.Probes && probes is { } shown)
            _probes.RecordDraw(view, shown);

        _pixelEffects.Record(view, frame.Index, effects, traced);
        _surfaceView.Record(view, inputs.DebugView);

        GpuTexture target = graph.WindowTarget;
        if (!target.IsNone)
            graph.Blit(view.Output, target);

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
    /// Numbers the world anew, so <see cref="Render"/> forgets what was drawn before at the first frame of what comes. On
    /// the game thread, without leaving it, so the world goes on to what comes before another frame is published and no
    /// frame shows it empty between the two.
    /// </summary>
    public Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        _worldNumber++;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks for every model and material <paramref name="entities"/> draw, and the effects of their first weather and
    /// first post processing, or the default's, and ends in the frame they are on the GPU, or failed and drawn as the
    /// failure or left out, with the passes made. Materials are asked for at once; models once the pools are made, and
    /// the effects, by <see cref="Render"/>. One that comes while another waits ends that one. On the render thread,
    /// whose tables these are.
    /// </summary>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        if (!_started.Ok)
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

        // The effects as Render runs them: the first weather's, then the first post processing's, or the default's.
        MaterialSlots air = entities
            .SelectMany(entity => entity.OfType<Weather>())
            .Select(weather => weather.Effects)
            .FirstOrDefault();
        MaterialSlots post = entities
            .SelectMany(entity => entity.OfType<PostProcessing>())
            .Select(processing => processing.Effects)
            .DefaultIfEmpty(_defaultPost.Effects)
            .First();

        EndPreparing();
        Preparing preparing = new(
            [.. renderers.Select(renderer => renderer.Model).Distinct()],
            materials,
            [air, post],
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
        RenderSun sun = new(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One);
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
            sun = new RenderSun(towards, illuminance);
            sunRank = rank;
        }

        return sun;
    }

    /// <summary>
    /// Asks for the models and the effects <paramref name="preparing"/> waits for, which the pools only take once they are
    /// made, and for the failure material a model that failed is drawn with, an ask for one asked for already doing
    /// nothing; then whether every one of them and every material is on the GPU, or failed: drawn as the failure, which
    /// is on the GPU too, or, for an effect, left out.
    /// </summary>
    private bool IsPrepared(RenderGraph graph, Preparing preparing)
    {
        bool isPrepared = !_pixelEffects.IsLoading(graph, preparing.Effects.AsSpan());
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
        in Frame frame,
        Vector2 jitter,
        int instanceCount
    )
    {
        // The world's structure is asked for every frame, so it stays, and built again only when the rows changed, before
        // their upload marks them unchanged; a model's arrival builds every row again, so it builds the structure too.
        TracedWorld? traced = null;
        if (_rayTracing is not null)
        {
            Result<TracedWorld> world = RayTracing.Prepare(graph);
            if (!world.Ok)
                return Result.Failure(world.Error);

            traced = world.Value;
            if (_instances.HasChanges)
                _rayTracing.Build(graph, world.Value, _instances.Rows, _instances.Extras);
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
            frame,
            jitter,
            inputs.Width,
            inputs.Height,
            instanceCount,
            tables.Value.Rows,
            tables.Value.Materials,
            cull.Value,
            _rayTracing?.Acceleration ?? GpuAcceleration.None,
            traced?.HitMaterials ?? GpuBuffer.None,
            inputs.ImpostorPixels,
            inputs.ImpostorDistance
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

        // The sky's tables and the shadow mask before the frame's lights and shadows, which name them, and while there is
        // a world to trace, the traced reflections.
        Result<SkyTables> sky = SkyLuts.Prepare(graph);
        if (!sky.Ok)
            return Result.Failure(sky.Error);

        Result<ShadowBuffers>? shadows = _shadowMask?.Prepare(view.Value);
        if (shadows is { Ok: false } failed)
            return Result.Failure(failed.Error);

        bool isTraced = _rayTracing is { Acceleration.IsNone: false };
        Result<TracedReflectionBuffers>? reflections = isTraced
            ? TracedReflections.Prepare(view.Value, frame.Index)
            : null;
        if (reflections is { Ok: false } unreflected)
            return Result.Failure(unreflected.Error);

        Result lit = Lighting.Upload(view.Value, inputs.Skies, camera, sun, sky.Value, lights.Value, shadows?.Value);
        if (!lit.Ok)
            return Result.Failure(lit.Error);

        // The probes while there is a world to trace.
        Result<ProbeBuffers>? probes = isTraced ? _probes?.Prepare(view.Value, _instances.Extras) : null;
        if (probes is { Ok: false } unprobed)
            return Result.Failure(unprobed.Error);

        // The traced diffuse light, whose rays read the probes beyond their range, while there are probes.
        Result<TracedDiffuseBuffers>? diffuse = probes is { Ok: true }
            ? TracedDiffuse.Prepare(view.Value, frame.Index)
            : null;
        if (diffuse is { Ok: false } untraced)
            return Result.Failure(untraced.Error);

        // The GI constants, each part empty where it isn't drawn, in one row after the lights and the shadows.
        GIConstants gi = new()
        {
            Grid = probes?.Value.Constants ?? default,
            Diffuse = diffuse?.Value.Constants ?? default,
            Reflections = reflections?.Value.Constants ?? default,
        };
        graph.Upload(view.Value.Constants, [gi], GIConstants.Offset);

        return Result.Success(
            new UploadedFrame(
                view.Value,
                sky.Value,
                lights.Value,
                shadows?.Value,
                traced,
                probes?.Value,
                diffuse?.Value,
                reflections?.Value
            )
        );
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

        graph.ReadBack(view.Output, readback.Value);
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

        if (setting.Key == "GIRendering.impostorPixels")
        {
            if (float.TryParse(setting.Value, CultureInfo.InvariantCulture, out float pixels) && pixels >= 0)
                _impostorPixels = pixels;
            else
                Log.Error($"{setting.Key} takes a pixel radius of 0 or more, not '{setting.Value}'.");

            return;
        }

        if (setting.Key == "GIRendering.impostorDistance")
        {
            if (float.TryParse(setting.Value, CultureInfo.InvariantCulture, out float metres) && metres >= 0)
                _impostorDistance = metres;
            else
                Log.Error($"{setting.Key} takes a distance in metres of 0 or more, not '{setting.Value}'.");

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
    /// What the frame's upload hands its passes: the view, the sky's tables, the lights, and on a GPU that traces rays the
    /// shadow mask, the world as rays see it, the probes, the traced diffuse light and the traced reflections, while there
    /// is a world.
    /// </summary>
    private readonly record struct UploadedFrame(
        RenderView View,
        SkyTables Sky,
        LightBuffers Lights,
        ShadowBuffers? Shadows,
        TracedWorld? Traced,
        ProbeBuffers? Probes,
        TracedDiffuseBuffers? Diffuse,
        TracedReflectionBuffers? Reflections
    );

    /// <summary>
    /// The models and materials a domain loading draws, the lists of effects its frames run, and what ends once they are
    /// all on the GPU.
    /// </summary>
    private sealed record Preparing(
        ImmutableArray<Handle<Model>> Models,
        ImmutableArray<Handle<Material>> Materials,
        ImmutableArray<MaterialSlots> Effects,
        TaskCompletionSource Done
    );
}
