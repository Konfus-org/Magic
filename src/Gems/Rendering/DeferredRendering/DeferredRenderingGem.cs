using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.DeferredRendering.Post;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Graphics;
using Magic.Input;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.DeferredRendering;

/// <summary>
/// What the view's step takes each frame: the upscaling, where a gem makes it.
/// </summary>
public readonly record struct ViewInput(Upscaling? Upscaling);

/// <summary>
/// What the shading's step takes each frame: the world as the GPU holds it, and, where a gem makes them, the world as
/// rays see it, each pixel's shadows, the light that reaches the surfaces indirectly, the air between the camera and
/// them, and the output's upscaling.
/// </summary>
public readonly record struct ShadingInput(
    GpuView View,
    TracedWorld? Traced,
    ShadowMask? Shadows,
    IndirectLight? Light,
    Air? Air,
    Upscaling? Upscaling
);

/// <summary>
/// The renderer, in two steps a frame. The view's step keeps the world on the GPU for the gems that draw it:
/// <c>LateUpdate</c> copies what the frame draws out of the entities and publishes it; on the render thread the step
/// takes the newest published, puts the models, materials, textures, instances, lights and sky's tables on the GPU,
/// uploads the frame's rows, and makes the frame's <see cref="GpuView"/> and its <see cref="GpuInstances"/>. The
/// shading's step lights that world, deferred: from the view and the GBuffer a geometry gem drew into it, the shading,
/// shadowed by the frame's shadow mask where there is one, the nearest glass over it, the effects, a debug view where
/// one is asked for, and the output into the window and any screenshot asked for. Where nothing made the world as rays
/// see it, the shadows, the indirect light or the air, their rows of the frame's buffer are 0. As a domain replaces the
/// world, it forgets the view's history and holds the loading screen until what comes is on the GPU and the passes are
/// built. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the
/// gem.
/// </summary>
public sealed class DeferredRenderingGem
    : IGem,
        IHasSettings<DeferredRenderingSettings>,
        IRender<ViewInput, (GpuView, GpuInstances)>,
        IRenderFrom<ShadingInput>,
        IHookIntoDomainLoading,
        IDisposable
{
    private const int DefaultWidth = 1280;

    private const int DefaultHeight = 720;

    private const int AutoRenderWidth = 1920;

    private const int AutoRenderHeight = 1080;

    private readonly IEcs _ecs;

    private readonly AssetManager _assets;

    private readonly Threads _threads;

    private readonly Mailbox<RenderInputs> _inputs = new();

    private readonly GpuResources _resources;

    private readonly InstanceTable _instances;

    /// <summary>
    /// The material a model that failed is drawn with, every slot; an empty slot when there is no failure material.
    /// </summary>
    private readonly MaterialInstance _failure;

    /// <summary>
    /// What is done to the picture when no entity has a <see cref="PostProcessing"/>: the exposure and the AgX tonemap.
    /// </summary>
    private readonly PostProcessing _defaultPost;

    private readonly FrameView _frameView;

    private readonly SkyLuts _sky;

    private readonly LightTiles _lightTiles;

    private readonly Effects _effects;

    /// <summary>
    /// Every shader the view's passes run, which the GPU provider builds before the frame is made.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _viewShaders;

    private readonly Subscription _preprocessing;

    /// <summary>
    /// The number of the world the frames <see cref="LateUpdate"/> publishes are of, a new one each time a domain
    /// replaces it, so the view's step forgets the view's history at the first frame of what comes. Game thread.
    /// </summary>
    private int _worldNumber;

    /// <summary>
    /// The number of the world the view's step drew last, none before the first frame. Render thread.
    /// </summary>
    private int? _drawnWorldNumber;

    /// <summary>
    /// Whether the gem makes views: failed once a shader or a pool is not made. Render thread.
    /// </summary>
    private Result _started = Result.Success();

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until the view's step has uploaded it all. Render
    /// thread.
    /// </summary>
    private Preparing? _preparing;

    /// <summary>
    /// The domains that loaded whose models, materials and effects are on the GPU, or failed: what
    /// <see cref="GpuView.PreparedDomains"/> counts. Render thread.
    /// </summary>
    private int _preparedDomains;

    private readonly IGraphicsApi _gpu;
    private readonly EventCoordinator _events;
    private readonly Channel<ScreenshotRequested> _screenshots = Channel.CreateUnbounded<ScreenshotRequested>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly ShaderEntry _shadeShader;
    private readonly ShaderEntry _translucentShader;
    private readonly ShaderEntry _lightShader;
    private readonly ShaderEntry _showShader;
    private readonly PixelEffects _pixelEffects = new();
    private readonly RendererDebugWindow? _debugWindow;

    /// <summary>
    /// Every shader the shading's passes run, which the GPU provider builds before the frame is drawn.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shadingShaders;
    private bool _hasDrawn;

    /// <summary>
    /// How many domains have loaded since the start, and what the newest waits on before it shows: the gem's passes
    /// built, once the view counts it prepared. Render thread.
    /// </summary>
    private int _domainsLoaded;
    private TaskCompletionSource? _loading;

    public DeferredRenderingGem(
        DeferredRenderingSettings settings,
        IShaderCompiler shaders,
        IGraphicsApi gpu,
        AssetManager assets,
        IEcs ecs,
        Threads threads,
        EventCoordinator events,
        IDebugUI? debugUI = null,
        IInput? input = null
    )
    {
        Settings = settings;
        _ecs = ecs;
        _assets = assets;
        _threads = threads;
        ModelPreprocessor models = new();
        _preprocessing = assets.Register(models);
        _resources = new GpuResources(
            models,
            assets,
            shaders,
            threads,
            settings.Anisotropy,
            settings.DroppedTextureLevels
        );
        _failure = new MaterialInstance { Handle = assets.GetFailure<Material, Model>() };
        _defaultPost.Effects[0] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Exposure.mat") };
        _defaultPost.Effects[1] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Agx.mat") };
        _instances = new InstanceTable(assets, _resources, _failure);
        _frameView = new FrameView();
        _sky = new SkyLuts(assets);
        _lightTiles = new LightTiles(assets);
        _effects = new Effects(assets, _resources, shaders, threads);
        _viewShaders = [.. _sky.Shaders, .. _lightTiles.Shaders];

        _debugWindow = debugUI is null ? null : new RendererDebugWindow(debugUI, input);
        _gpu = gpu;
        _events = events;
        _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
        {
            TypeArguments = [GpuView.GetAtmosphere(assets)],
        };
        _translucentShader = PassShaders.GetEntry(assets, "ShadeTranslucent", "ShadeTranslucent") with
        {
            TypeArguments = [GpuView.GetAtmosphere(assets)],
        };
        _lightShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowLight");
        _showShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowSurface");
        _shadingShaders = [_shadeShader, _translucentShader, _lightShader, _showShader];
    }

    /// <summary>
    /// How the world is drawn and what the renderer shows, which the debug window also sets on the game thread.
    /// <see cref="LateUpdate"/> hands it each frame to the view's step; the shading's step reads it on the render
    /// thread: a record, written whole, which no one changes.
    /// </summary>
    public DeferredRenderingSettings Settings { get; set; }

    public void Dispose()
    {
        _preprocessing.Dispose();
        EndPreparing();
    }

    /// <summary>
    /// Draws the debug window, which sets the settings.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (_debugWindow is not null)
            Settings = _debugWindow.Update(Settings);
    }

    /// <summary>
    /// Copies what the frame draws out of the entities and publishes it for the view's step, and hands the shading's
    /// step the screenshots asked for.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        RenderInputs back = _inputs.Back;
        back.Settings = Settings;
        _ecs.Snapshot(back.Renderers);
        _ecs.Snapshot(back.Cameras);
        _ecs.Snapshot(back.DirectionalLights);
        _ecs.Snapshot(back.PointLights);
        _ecs.Snapshot(back.SpotLights);
        _ecs.Snapshot(back.RectLights);
        _ecs.Snapshot(back.Skies);
        _ecs.Snapshot(back.PostProcessing);
        back.WorldNumber = _worldNumber;
        _inputs.Publish();

        foreach (ScreenshotRequested asked in frame.Events.Get<ScreenshotRequested>())
        {
            _screenshots.Writer.TryWrite(asked);
        }
    }

    /// <summary>
    /// The newest inputs published, or the last again when none are newer, on the GPU, and the frame's view of them and
    /// its instances; none while the shaders build, the pools are not made, or nothing was published yet.
    /// </summary>
    public (GpuView, GpuInstances)? Render(in Frame frame, RenderGraph graph, in ViewInput input)
    {
        if (!_started.Ok)
            return null;

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails ends the start.
        GpuState shaders = graph.GetState(_viewShaders.AsSpan());
        if (shaders == GpuState.Failed)
            FailStart("a shader was not built.");

        if (shaders != GpuState.Ready)
            return null;

        // The pools, with the models made since the last frame settled into them, before any renderer's row is built.
        // Every model asked for from here on is asked at or after the frame's start, which eviction keeps.
        long frameStart = Environment.TickCount64;
        Result pools = _resources.Prepare(graph);
        if (!pools.Ok)
        {
            FailStart(pools.Error);

            return null;
        }

        if (_inputs.TakeNewest() is not { } inputs)
            return null;

        // The first frame of another world forgets last frame's camera and size, so nothing drawn before carries
        // into it.
        if (inputs.WorldNumber != _drawnWorldNumber)
        {
            _frameView.ClearHistory();
            _drawnWorldNumber = inputs.WorldNumber;
        }

        // Entities -> resources -> rows: the renderers written since the inputs before ask for their models and have
        // their rows built again, and the materials' rows are made again at upload.
        int instanceCount = _instances.Fill(inputs.Renderers, frame.Index);

        // The loading domain shows once what it draws and its effects are on the GPU.
        if (_preparing is { } preparing && IsPrepared(graph, preparing))
        {
            Log.Info(
                $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
            );
            EndPreparing();
        }

        // A model no renderer drawn holds, and nothing asked for this frame, leaves once it is kept long enough; the
        // loading domain's models were just asked for, so they stay while it waits.
        _resources.EvictUnusedModels(
            inputs.Renderers.Second[..instanceCount],
            frameStart,
            inputs.Settings.ModelKeepSec
        );

        _resources.Upload(graph);

        // The picture's effects; the camera jitters while one of them asks for it, or while a gem upscales.
        Result<(ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows)> effects = _effects.Resolve(
            graph,
            GetEffects(inputs)
        );
        if (!effects.Ok)
        {
            Log.Error($"The frame's view is not made: {effects.Error}");

            return null;
        }

        bool isJittered = input.Upscaling is not null;
        foreach (GpuEffect effect in effects.Value.Effects.Span)
        {
            isJittered |= effect.State == GpuState.Ready && effect.Needs.HasJitter;
        }

        Vector2 jitter = isJittered ? FrameView.GetJitter(frame.Index) : Vector2.Zero;

        Result<GpuView> view = UploadFrame(graph, inputs, frame, jitter, instanceCount, effects.Value);
        if (!view.Ok)
        {
            Log.Error($"The frame's view is not made: {view.Error}");

            return null;
        }

        // The sky's tables while there is a sky, and each tile's lights.
        GpuConstants header = new(view.Value.Frame.Id);
        if (inputs.Skies.Count > 0)
            _sky.Record(graph, view.Value, header);

        _lightTiles.Record(graph, view.Value, header);
        return (view.Value, _instances.Instances);
    }

    /// <summary>
    /// Records the frame's passes, each in its stage, from the view the view's step made.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in ShadingInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each. While one failed, the renderer draws
        // nothing and holds no domain back; else the loading domain shows once its models and materials are on the GPU.
        GpuState shaders = graph.GetState(_shadingShaders.AsSpan());
        GpuView gpu = input.View;
        if (
            _loading is { } loading
            && (shaders == GpuState.Failed || (shaders == GpuState.Ready && gpu.PreparedDomains >= _domainsLoaded))
        )
        {
            loading.TrySetResult();
            _loading = null;
        }

        if (shaders != GpuState.Ready)
            return;

        DeferredRenderingSettings settings = Settings;

        ReadOnlySpan<GpuResource> indirect = input.Light is { } light
            ? [light.Diffuse, light.Reflections, light.Probes, light.ProbeDepth]
            : [];

        // Every pixel's surface shaded from the sky's tables, its tile's lights, the shadows where there are any and the
        // indirect light, or the sky where nothing traced it.
        ComputePass shade = graph
            .Compute("Shade surfaces", _shadeShader, new GpuConstants(gpu.Frame.Id), RenderStage.Shade)
            .Reads(
                gpu.Frame,
                gpu.SkyTransmittance,
                gpu.SkyMultiScattering,
                gpu.SkyView,
                gpu.LightTable,
                gpu.LightTiles,
                gpu.Depth,
                gpu.GBufferNormal,
                gpu.GBufferBase,
                gpu.GBufferSurface
            )
            .Reads(indirect);
        if (input.Shadows is { } shadows)
            shade.Reads(shadows.Mask, shadows.SunTint);

        if (input.Air is { Clouds: { } clouds })
            shade.Reads(clouds);

        shade.Writes(gpu.Hdr).DispatchOver(gpu.Width, gpu.Height);

        // The nearest pane over the shaded picture, from what the resolve left in the glass targets, with its rays
        // through the traced world where there is one, and the air in front of it or of the opaque surface; nothing to
        // do with neither glass nor air.
        if (!gpu.GlassDepth.IsNone || input.Air is not null)
        {
            ComputePass translucent = graph
                .Compute("Shade translucent", _translucentShader, new GpuConstants(gpu.Frame.Id), RenderStage.Shade)
                .Reads(
                    gpu.Frame,
                    gpu.SkyTransmittance,
                    gpu.SkyMultiScattering,
                    gpu.SkyView,
                    gpu.Depth,
                    gpu.GlassDepth,
                    gpu.GlassBase,
                    gpu.GlassNormal,
                    gpu.GlassSurface
                )
                .Reads(indirect);
            if (input.Traced is { } traced)
                translucent.Reads(traced.HitMaterials, gpu.Meshes, gpu.Instances);

            if (input.Air is { } air)
                translucent.Reads(air.Volume);

            translucent.Writes(gpu.Hdr).DispatchOver(gpu.Width, gpu.Height);
        }

        // A debug view of light draws it into the picture in place of the lit one, black where none was traced, so the
        // effects finish it as they do the picture.
        if (settings.RenderView is DebugView.IndirectDiffuse or DebugView.Reflections)
        {
            graph
                .Compute(
                    "Show light",
                    _lightShader,
                    new GpuConstants(gpu.Frame.Id, (uint)settings.RenderView),
                    RenderStage.Shade
                )
                .Reads(gpu.Frame, gpu.Depth, gpu.GBufferNormal)
                .Reads(indirect)
                .Writes(gpu.Hdr)
                .DispatchOver(gpu.Width, gpu.Height);
        }

        _pixelEffects.Record(graph, gpu, frame.Index);

        // The output drawn as the debug view asks, a Depth view black from its far distance, and the picture's middle
        // tones shown at the gamma.
        graph
            .Compute(
                "Show surface",
                _showShader,
                new GpuConstants(
                    gpu.Frame.Id,
                    (uint)settings.RenderView,
                    BitConverter.SingleToUInt32Bits(settings.DepthViewFar),
                    BitConverter.SingleToUInt32Bits(Math.Clamp(settings.Gamma, 0.5f, 2f))
                ),
                RenderStage.Post
            )
            .Reads(gpu.Frame, gpu.Depth, gpu.GBufferNormal, gpu.GBufferBase, gpu.GBufferSurface, gpu.Velocity, gpu.Hdr)
            .Writes(gpu.Output)
            .DispatchOver(gpu.Width, gpu.Height);

        // What the window and a screenshot show: the output as a gem upscaled it to the size it is shown at, or the
        // output itself, stretched over the window with the upscale filter.
        GpuTexture upscaled = input.Upscaling is { } upscaling ? upscaling.Record(graph, gpu) : GpuTexture.None;
        GpuTexture shown = upscaled.IsNone ? gpu.Output : upscaled;
        GpuTexture target = graph.WindowTarget;
        if (!target.IsNone)
            graph.Blit(shown, target, RenderStage.Post, settings.UpscaleFilter);

        // One readback buffer, so one screenshot a frame: the last asked for.
        ScreenshotRequested? shot = null;
        while (_screenshots.Reader.TryRead(out ScreenshotRequested asked))
        {
            shot = asked;
        }

        if (shot is { } saved)
            SaveScreenshot(graph, shown, gpu.ShownWidth, gpu.ShownHeight, settings.UpscaleFilter, saved.Path);

        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }
    }

    /// <summary>
    /// Numbers the world anew, so the view's step forgets the view's history at the first frame of what comes. On
    /// the game thread, without leaving it, so the world goes on to what comes before another frame is published.
    /// </summary>
    public Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        _worldNumber++;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Holds the domain loading until it can show. First the view's part: asks for every model and material
    /// <paramref name="entities"/> draw, and the effects of their first post processing, or the default's, and waits
    /// until they are on the GPU, or failed and drawn as the failure or left out. Materials are asked for at once; models
    /// once the pools are made, and the effects, by the view's step. Then the shading's part: waits until its passes are
    /// built, once the view counts the domain prepared. One that comes while another waits ends that one. On the render
    /// thread, whose tables and counts these are.
    /// </summary>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        EndPreparing();
        if (!_started.Ok)
        {
            _preparedDomains++;
        }
        else
        {
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

            // The effects as Render runs them: the first post processing's, or the default's.
            MaterialSlots post = entities
                .SelectMany(entity => entity.OfType<PostProcessing>())
                .Select(processing => processing.Effects)
                .DefaultIfEmpty(_defaultPost.Effects)
                .First();

            Preparing preparing = new(
                [.. renderers.Select(renderer => renderer.Model).Distinct()],
                materials,
                post,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            _preparing = preparing;
            await preparing.Done.Task.WaitAsync(cancel).ConfigureAwait(false);
        }

        await _threads.Enter(EngineThread.Render);
        _domainsLoaded++;
        _loading?.TrySetResult();
        _loading = null;
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = loading;
        await loading.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The first camera that draws into the window, else one at the origin.
    /// </summary>
    private static (Transform Transform, Camera Camera) GetCamera(EcsSnapshot<Transform, Camera> cameras)
    {
        for (int i = 0; i < cameras.Count; i++)
        {
            if (cameras.Second[i].Target.IsWindow)
                return (cameras.First[i], cameras.Second[i]);
        }

        return (new Transform(), new Camera());
    }

    /// <summary>
    /// The directional light the frame is lit by: the sun while it is up, else the moon while it is up, else the first
    /// there is, else a white light from above: towards it, unit, and its colour times its intensity, in lux, above the
    /// air.
    /// </summary>
    private static (Vector3 Direction, Vector3 Illuminance) GetSun(EcsSnapshot<Transform, DirectionalLight> lights)
    {
        (Vector3 Direction, Vector3 Illuminance) sun = (Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One);
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
            sun = (towards, illuminance);
            sunRank = rank;
        }

        return sun;
    }

    /// <summary>
    /// The effects the frame runs: the first post processing's, or the engine's, exposed by the exposure compensation;
    /// each whose material the settings turn off left out.
    /// </summary>
    private MaterialSlots GetEffects(RenderInputs inputs)
    {
        MaterialSlots effects;
        if (inputs.PostProcessing.Count > 0)
            effects = inputs.PostProcessing.First[0].Effects;
        else
        {
            effects = _defaultPost.Effects;
            effects[0].Overrides["compensation"] = inputs.Settings.ExposureCompensation;
        }

        // Most frames turn nothing off, so they name no material.
        ImmutableArray<string> disabled = inputs.Settings.DisabledEffects;
        if (disabled.IsEmpty)
            return effects;

        for (int place = 0; place < MaterialSlots.Capacity; place++)
        {
            if (
                _assets.GetPath(effects[place].Handle) is { } path
                && disabled.Contains(Path.GetFileNameWithoutExtension(path))
            )
                effects[place] = default;
        }

        return effects;
    }

    /// <summary>
    /// Asks for the models and the effects <paramref name="preparing"/> waits for, which the pools only take once they are
    /// made, and for the failure material a model that failed is drawn with, an ask for one asked for already doing
    /// nothing; then whether every one of them and every material is on the GPU, or failed: drawn as the failure, which
    /// is on the GPU too, or, for an effect, left out.
    /// </summary>
    private bool IsPrepared(RenderGraph graph, Preparing preparing)
    {
        bool isPrepared = !_effects.IsLoading(graph, preparing.Effects);
        bool hasFailure = false;
        foreach (Handle<Model> model in preparing.Models)
        {
            _resources.Get(model);
            isPrepared &= _resources.HasArrived(model);
            hasFailure |= _resources.HasFailed(model);
        }

        foreach (Handle<Material> material in preparing.Materials)
        {
            isPrepared &= _resources.Get(material) != GpuState.Loading;
        }

        if (!hasFailure || _failure.Handle == Handle<Material>.None)
            return isPrepared;

        return _resources.Get(_failure.Handle) != GpuState.Loading && isPrepared;
    }

    /// <summary>
    /// Lets the domain waiting on what it draws show, and counts it prepared.
    /// </summary>
    private void EndPreparing()
    {
        if (_preparing is null)
            return;

        _preparing.Done.TrySetResult();
        _preparing = null;
        _preparedDomains++;
    }

    /// <summary>
    /// Ends the start as failed: the gem makes no view, and holds no domain back.
    /// </summary>
    private void FailStart(string error)
    {
        Log.Error($"The world is not put on the GPU: {error}");
        EndPreparing();
        _started = Result.Failure(error);
    }

    /// <summary>
    /// The frame's tables and rows uploaded, each asking for what it uploads into, and the view that names them; what
    /// was not made, and no view is.
    /// </summary>
    private Result<GpuView> UploadFrame(
        RenderGraph graph,
        RenderInputs inputs,
        in Frame frame,
        Vector2 jitter,
        int instanceCount,
        (ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows) effects
    )
    {
        Result<(GpuBuffer Rows, GpuBuffer Materials)> tables = _instances.Upload(graph);
        if (!tables.Ok)
            return Result.Failure(tables.Error);

        // The finished picture at the resolution, or at the window's size when it has no side, and 1280 by 720 with no
        // window; the world drawn at the render scale of it, or when that is auto at the most that fits 1920 by 1080,
        // never more than all of it.
        Size shown = inputs.Settings.Resolution;
        if (shown.Width <= 0 || shown.Height <= 0)
        {
            TextureDescription window = graph.WindowTarget.IsNone ? default : graph.GetDescription(graph.WindowTarget);
            shown = window.Width > 0 ? new Size(window.Width, window.Height) : new Size(DefaultWidth, DefaultHeight);
        }

        float scale = MathF.Min(
            1,
            inputs.Settings.RenderScale > 0
                ? inputs.Settings.RenderScale
                : MathF.Min((float)AutoRenderWidth / shown.Width, (float)AutoRenderHeight / shown.Height)
        );
        int width = Math.Max(1, (int)MathF.Round(shown.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(shown.Height * scale));

        (Transform Transform, Camera Camera) camera = GetCamera(inputs.Cameras);
        if (inputs.Settings.FieldOfView > 0)
            camera.Camera = camera.Camera with { FieldOfView = inputs.Settings.FieldOfView };

        // The frame's buffer, the view's targets, the light table and its tiles, and the sky's tables asked for in that
        // order; the glass targets only while a material draws translucent, so the graph frees them in a world with
        // none.
        bool hasGlass = _resources.HasTranslucent;
        Result<GpuBuffer> constants = graph.Buffer(GpuDescriptions.GetBuffer("Frame constants", 1024));
        Result<GpuTexture> depth = graph.Texture(
            GpuDescriptions.GetTexture(
                "Depth",
                width,
                height,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            )
        );
        Result<GpuTexture> glassDepth = GetGlass(
            GpuDescriptions.GetTexture(
                "Glass depth",
                width,
                height,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            )
        );
        Result<GpuTexture> glassBase = GetGlass(GetTarget("Glass base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> glassNormal = GetGlass(GetTarget("Glass normal", GpuFormat.R16G16Unorm));
        Result<GpuTexture> glassSurface = GetGlass(GetTarget("Glass surface", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> normal = graph.Texture(GetTarget("GBuffer normal", GpuFormat.R16G16Unorm));
        Result<GpuTexture> baseColor = graph.Texture(GetTarget("GBuffer base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> surface = graph.Texture(GetTarget("GBuffer surface", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> velocity = graph.Texture(GetTarget("Velocity", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> hdr = graph.Texture(
            GpuDescriptions.GetTexture(
                "HDR",
                width,
                height,
                GpuFormat.R16G16B16A16Sfloat,
                TextureUsages.Storage
                    | TextureUsages.ColorTarget
                    | TextureUsages.Sampled
                    | TextureUsages.CopyDestination
            )
        );
        Result<GpuTexture> output = graph.Texture(
            GpuDescriptions.GetTexture(
                "Output",
                width,
                height,
                GpuFormat.R8G8B8A8Unorm,
                TextureUsages.Storage | TextureUsages.ColorTarget | TextureUsages.Sampled | TextureUsages.CopySource
            )
        );
        Result<LightBuffers> lights = _lightTiles.Prepare(
            graph,
            width,
            height,
            inputs.PointLights,
            inputs.SpotLights,
            inputs.RectLights
        );
        Result<GpuTexture> transmittance = graph.Texture(GetSkyTable("Sky transmittance", 256, 64));
        Result<GpuTexture> multiScattering = graph.Texture(GetSkyTable("Sky multi-scattering", 32, 32));
        Result<GpuTexture> skyView = graph.Texture(GetSkyTable("Sky view", 192, 108));
        Result asked = Result.FindFailure(
            constants,
            depth,
            glassDepth,
            glassBase,
            glassNormal,
            glassSurface,
            normal,
            baseColor,
            surface,
            velocity,
            hdr,
            output,
            lights,
            transmittance,
            multiScattering,
            skyView
        );
        if (!asked.Ok)
            return Result.Failure(asked.Error);

        GpuView view = new()
        {
            Frame = constants.Value,
            Width = width,
            Height = height,
            ShownWidth = shown.Width,
            ShownHeight = shown.Height,
            HasHistory = _frameView.HasHistory(width, height),
            Depth = depth.Value,
            GBufferNormal = normal.Value,
            GBufferBase = baseColor.Value,
            GBufferSurface = surface.Value,
            Velocity = velocity.Value,
            GlassDepth = glassDepth.Value,
            GlassBase = glassBase.Value,
            GlassNormal = glassNormal.Value,
            GlassSurface = glassSurface.Value,
            Hdr = hdr.Value,
            Output = output.Value,
            Instances = tables.Value.Rows,
            InstanceCount = instanceCount,
            InstanceCapacity = _instances.Capacity,
            Materials = tables.Value.Materials,
            Vertices = _resources.VertexPool,
            Indices = _resources.IndexPool,
            Meshes = _resources.Meshes,
            Models = _resources.Models,
            MaterialShaders = _resources.MaterialShaders,
            HeapTextures = _resources.GetHeapTextures(),
            LightTable = lights.Value.Table,
            LightTiles = lights.Value.Tiles,
            SkyTransmittance = transmittance.Value,
            SkyMultiScattering = multiScattering.Value,
            SkyView = skyView.Value,
            Effects = effects.Effects,
            EffectRows = effects.Rows,
            PreparedDomains = _preparedDomains,
        };
        Result uploaded = _frameView.Upload(
            graph,
            view,
            lights.Value.Count,
            camera,
            frame,
            jitter,
            GetSun(inputs.DirectionalLights),
            inputs.Skies.Count > 0,
            inputs.Settings.SkyBrightness
        );
        if (!uploaded.Ok)
            return Result.Failure(uploaded.Error);

        return Result.Success(view);

        TextureDescription GetTarget(string name, GpuFormat format) =>
            GpuDescriptions.GetTexture(name, width, height, format);

        static TextureDescription GetSkyTable(string name, int width, int height) =>
            GpuDescriptions.GetTexture(name, width, height, GpuFormat.R16G16B16A16Sfloat);

        Result<GpuTexture> GetGlass(in TextureDescription glass) =>
            hasGlass ? graph.Texture(glass) : Result.Success(GpuTexture.None);
    }

    /// <summary>
    /// The picture the window shows read back at <paramref name="width"/> by <paramref name="height"/>, the size it is
    /// shown at, through a copy stretched to it with <paramref name="filter"/>, and of 8 bits a channel where it is
    /// another size or has more, and written to <paramref name="path"/> once the GPU is done with it.
    /// </summary>
    private void SaveScreenshot(
        RenderGraph graph,
        GpuTexture shown,
        int width,
        int height,
        TextureFilter filter,
        string path
    )
    {
        TextureDescription drawn = graph.GetDescription(shown);
        Result<GpuBuffer> readback = graph.Buffer(
            GpuDescriptions.GetBuffer("Output readback", (long)width * height * 4, BufferUsages.Readback)
        );
        Result<GpuTexture> read =
            drawn.Format == GpuFormat.R8G8B8A8Unorm && drawn.Width == width && drawn.Height == height
                ? Result.Success(shown)
                : graph.Texture(
                    GpuDescriptions.GetTexture(
                        "Screenshot copy",
                        width,
                        height,
                        GpuFormat.R8G8B8A8Unorm,
                        TextureUsages.CopyDestination | TextureUsages.CopySource
                    )
                );
        if (!readback.Ok || !read.Ok)
        {
            Log.Error($"The screenshot was not taken: {(readback.Ok ? read.Error : readback.Error)}");

            return;
        }

        if (read.Value != shown)
            graph.Blit(shown, read.Value, RenderStage.Post, filter);

        graph.ReadBack(read.Value, readback.Value, RenderStage.Post);
        SaveScreenshotAsync(readback.Value, path, width, height).FireAndForget();
    }

    /// <summary>
    /// Writes what was read back to <paramref name="path"/> once the GPU is done with it, opaque as the window shows it,
    /// as a texture through the gem that writes them.
    /// </summary>
    private async Task SaveScreenshotAsync(GpuBuffer readback, string path, int width, int height)
    {
        Result<ReadOnlyMemory<byte>> pixels = await _gpu.ReadBackAsync(
                readback,
                0,
                width * height * 4,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (!pixels.Ok)
        {
            Log.Error($"The screenshot could not be read back: {pixels.Error}");

            return;
        }

        // The window shows no alpha, which a picture may use for its own data.
        byte[] opaque = pixels.Value.ToArray();
        for (int alpha = 3; alpha < opaque.Length; alpha += 4)
        {
            opaque[alpha] = byte.MaxValue;
        }

        Texture shot = new()
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba8Srgb,
            Pixels = opaque,
            Levels = [new TextureLevel(width, height, 0, opaque.Length)],
            HasMipmaps = false,
            IsCompressed = false,
        };
        Result written = await _assets.ExportAsync(shot, path, null, CancellationToken.None).ConfigureAwait(false);
        if (written.Ok)
            Terminal.Write(path);
        else
            Log.Error($"The screenshot could not be written: {written.Error}");
    }

    /// <summary>
    /// The models and materials a domain loading draws, the effects its frames run, and what ends once they are all on
    /// the GPU.
    /// </summary>
    private sealed record Preparing(
        ImmutableArray<Handle<Model>> Models,
        ImmutableArray<Handle<Material>> Materials,
        MaterialSlots Effects,
        TaskCompletionSource Done
    );
}
