using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.Post;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.DeferredRendering;

/// <summary>
/// What the view's step takes for each view: the world as the GPU holds it, the frame's views, and, where a gem makes
/// them, the world as rays see it and the upscaling.
/// </summary>
public readonly record struct ViewInput(GpuWorld World, RenderViews Views, TracedWorld? Traced, Upscaling? Upscaling);

/// <summary>
/// What the shading's step takes for each view: the world as the GPU holds it, the frame's views, the view, and the
/// inputs that other gems make.
/// </summary>
/// <remarks>
/// Where a gem makes them, it also takes the world as rays see it, the sky's tables, each pixel's shadows, the light
/// that reaches the surfaces indirectly, the air between the camera and them, and the output's upscaling.
/// </remarks>
public readonly record struct ShadingInput(
    GpuWorld World,
    RenderViews Views,
    GpuView View,
    TracedWorld? Traced,
    SkyTables? Sky,
    ShadowMask? Shadows,
    IndirectLight? Light,
    Air? Air,
    Upscaling? Upscaling
);

/// <summary>
/// Keeps the world on the GPU for the gems that draw it, and lights it with deferred shading, in four steps.
/// </summary>
/// <remarks>
/// <para>
/// The world's step runs once a frame: <see cref="PublishInputs"/> copies what the frame draws out of the entities and
/// publishes it. On the render thread, the step takes the newest published, puts the models, materials, textures,
/// render textures, instances and lights on the GPU, and makes the frame's <see cref="GpuWorld"/>, its
/// <see cref="GpuInstances"/> and its <see cref="RenderViews"/>. There is a view for each camera that draws into a
/// render texture, then one for each camera that draws into the window, each in its section, or one at the origin when
/// no camera does.
/// </para>
/// <para>
/// The view's step and the shading's step run once for each view. The view's step uploads the view's rows and makes
/// its <see cref="GpuView"/>.
/// </para>
/// <para>
/// The shading's step lights that world from the view and the GBuffer a geometry gem drew into it. It shades the
/// surfaces, shadowed by the view's shadow mask where there is one, then draws the nearest glass over them, the
/// effects, a debug view where one is asked for, and the output into the view's target and any screenshot asked for.
/// </para>
/// <para>
/// The banner's step draws the frame's render failures over the top of the window, even when no view was made.
/// </para>
/// <para>
/// Where nothing made the world as rays see it, the shadows, the indirect light or the air, their rows of the view's
/// buffer are 0. As a domain replaces the world, the renderer forgets each view's history and holds the loading screen
/// until what comes is on the GPU and the shading's shaders are built.
/// </para>
/// </remarks>
public sealed class Renderer
    : IRender,
        IRenderInto<(GpuWorld, GpuInstances, RenderViews)>,
        IRender<ViewInput, GpuView>,
        IRender<ShadingInput, FinalImage>,
        IHookIntoDomainLoading,
        IDisposable
{
    private const int DefaultWidth = 1280;

    private const int DefaultHeight = 720;

    private const int AutoRenderWidth = 1920;

    private const int AutoRenderHeight = 1080;

    /// <summary>
    /// How the world is drawn and what the renderer shows, which the debug window also sets on the game thread. Both
    /// steps read it on the render thread.
    /// </summary>
    private readonly DeferredRenderingSettings _settings;

    private readonly IEcs _ecs;

    private readonly AssetManager _assets;

    private readonly Threads _threads;

    private readonly Mailbox<RenderInputs> _inputs = new();

    private readonly GpuResources _resources;

    private readonly InstanceTable _instances;

    /// <summary>
    /// What is done to the picture when no entity has a <see cref="PostProcessing"/>: the exposure and the AgX tonemap.
    /// </summary>
    private readonly PostProcessing _defaultPost;

    /// <summary>
    /// What each view keeps from frame to frame, by its number: where it looked last frame, and its effects'
    /// histories. Render thread.
    /// </summary>
    private readonly List<(FrameView Frame, PixelEffects Effects)> _views = [];

    private readonly LightTiles _lightTiles;

    private readonly Effects _effects;

    /// <summary>
    /// Every shader the view's passes run, which the GPU provider builds before the frame is made.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _viewShaders;

    private readonly ShaderEntry _shadeShader;

    private readonly ShaderEntry _translucentShader;

    private readonly ShaderEntry _lightViewShader;

    private readonly ShaderEntry _showShader;

    /// <summary>
    /// Every shader the shading's passes run, which the GPU provider builds before the step draws.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shadingShaders;

    private readonly FailureBanner _banner;

    private readonly IGraphicsApi _gpu;

    private readonly EventCoordinator _events;

    /// <summary>
    /// The screenshots asked for on the game thread. The world's step looks at the first, and the shading's step of
    /// the last view takes it, on the render thread.
    /// </summary>
    private readonly Channel<ScreenshotRequested> _screenshots = Channel.CreateUnbounded<ScreenshotRequested>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// The ids of the assets read again, heard on the game thread, which the world's step lets go of on the render
    /// thread.
    /// </summary>
    private readonly Channel<ulong> _reloaded = Channel.CreateUnbounded<ulong>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    private readonly Subscription _preprocessing;

    /// <summary>
    /// The number of the world that <see cref="PublishInputs"/> publishes frames of, new each time a domain replaces
    /// it. Game thread.
    /// </summary>
    /// <remarks>
    /// A new number makes the world's step forget each view's history at the first frame of what comes.
    /// </remarks>
    private int _worldNumber;

    /// <summary>
    /// The number of the world the world's step drew last, none before the first frame. Render thread.
    /// </summary>
    private int? _drawnWorldNumber;

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until the world's step has uploaded it all. Render
    /// thread.
    /// </summary>
    private Preparing? _preparing;

    /// <summary>
    /// The domains that loaded whose models, materials and effects are on the GPU, or failed: what
    /// <see cref="GpuView.PreparedDomains"/> counts. Render thread.
    /// </summary>
    private int _preparedDomains;

    private bool _hasDrawn;

    internal Renderer(
        DeferredRenderingSettings settings,
        IShaderCompiler shaders,
        IGraphicsApi gpu,
        AssetManager assets,
        IEcs ecs,
        Threads threads,
        EventCoordinator events
    )
    {
        _settings = settings;
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
        _defaultPost.Effects[0] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Exposure.mat") };
        _defaultPost.Effects[1] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Agx.mat") };
        _instances = new InstanceTable(assets, _resources);
        _lightTiles = new LightTiles(assets);
        _effects = new Effects(assets, _resources);
        _banner = new FailureBanner(assets);
        _viewShaders = _lightTiles.Shaders;

        _gpu = gpu;
        _events = events;
        ImmutableArray<ShaderTypeArgument> atmosphere = [GpuView.GetAtmosphere(assets)];
        _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
        {
            TypeArguments = atmosphere,
        };
        _translucentShader = PassShaders.GetEntry(assets, "ShadeTranslucent", "ShadeTranslucent") with
        {
            TypeArguments = atmosphere,
        };
        _lightViewShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowLight");
        _showShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowSurface");
        _shadingShaders = [_shadeShader, _translucentShader, _lightViewShader, _showShader];
    }

    public void Dispose()
    {
        _preprocessing.Dispose();
        _banner.Dispose();
        EndPreparing();
    }

    /// <summary>
    /// Puts the newest inputs published, or the last again when none are newer, on the GPU, and counts the frame's
    /// views.
    /// </summary>
    /// <returns>
    /// The world, its instances and the frame's views; none while the shaders build, the pools are not made, or nothing
    /// was published yet.
    /// </returns>
    public (GpuWorld, GpuInstances, RenderViews)? Render(in Frame frame, RenderGraph graph)
    {
        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState shaders = graph.GetState(_viewShaders.AsSpan());
        if (shaders == GpuState.Failed)
            Fail(graph, RenderFailure.Shader, "a shader of the view was not built.");

        if (shaders != GpuState.Ready)
            return null;

        // What was made from an asset read again is let go, so the next ask loads it anew and its failure is found
        // again.
        while (_reloaded.Reader.TryRead(out ulong asset))
        {
            _resources.Forget(asset);
            _instances.Forget(asset);
        }

        // The pools, with the models made since the last frame settled into them, before any renderer's row is built.
        // Every model asked for from here on is asked at or after the frame's start, which eviction keeps.
        long frameStart = Environment.TickCount64;
        Result pools = _resources.Prepare(graph);
        if (!pools.Ok)
        {
            Fail(graph, RenderFailure.Resource, pools.Error);

            return null;
        }

        if (_inputs.TakeNewest() is not { } inputs)
            return null;

        // The first frame of another world forgets each view's camera and size of last frame, so nothing drawn before
        // carries into it.
        if (inputs.WorldNumber != _drawnWorldNumber)
        {
            foreach ((FrameView frameView, _) in _views)
            {
                frameView.ClearHistory();
            }

            _drawnWorldNumber = inputs.WorldNumber;
        }

        // Entities -> resources -> rows: the renderers written since the inputs before ask for their models and have
        // their rows built again, and the materials' rows are made again at upload.
        _instances.Fill(inputs.Renderers, frame.Index);

        // The loading domain shows once the models, materials and effects it waits for are on the GPU, or failed: a
        // model drawn as the failure cube, which is then waited for too, or, for an effect, left out; and once the
        // shading's shaders are built, or failed. Its models are asked for here, as the pools only take
        // them once they are made; an ask for one asked for already does nothing.
        if (_preparing is { } preparing)
        {
            bool isPrepared =
                !_effects.IsLoading(graph, preparing.Effects)
                && graph.GetState(_shadingShaders.AsSpan()) != GpuState.Loading;
            foreach (Handle<Model> model in preparing.Models)
            {
                _resources.Get(model);
                isPrepared &= _resources.HasArrived(model);
            }

            foreach (Handle<Material> material in preparing.Materials)
            {
                isPrepared &= _resources.Get(material) != GpuState.Loading;
            }

            if (isPrepared)
            {
                Log.Info(
                    $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
                );
                EndPreparing();
            }
        }

        // A model no renderer drawn holds, and nothing asked for this frame, leaves once it is kept long enough; the
        // loading domain's models were just asked for, so they stay while it waits.
        _resources.EvictUnusedModels(inputs.Renderers.Second[.._instances.Count], frameStart, _settings.ModelKeepSec);
        _resources.Upload(graph);

        // The picture's effects: the first post processing's, or the engine's, exposed by the exposure compensation;
        // each whose material the settings turn off left out, which most frames name none of.
        MaterialSlots post;
        if (inputs.PostProcessing.Count > 0)
            post = inputs.PostProcessing.First[0].Effects;
        else
        {
            post = _defaultPost.Effects;
            post[0].Overrides["compensation"] = _settings.ExposureCompensation;
        }

        ImmutableArray<string> disabled = _settings.DisabledEffects;
        for (int place = 0; place < MaterialSlots.Capacity && !disabled.IsEmpty; place++)
        {
            if (
                _assets.GetPath(post[place].Handle) is { } path
                && disabled.Contains(Path.GetFileNameWithoutExtension(path))
            )
                post[place] = default;
        }

        Result<(ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows)> effects = _effects.Resolve(graph, post);
        Result<(GpuBuffer Rows, GpuBuffer Materials, GpuBuffer Failures)> tables = _instances.Upload(graph);

        // The whole finished picture at the resolution, or at the window's size when it has no side, and 1280 by 720
        // with no window.
        Size picture = _settings.Resolution;
        if (picture.Width <= 0 || picture.Height <= 0)
        {
            TextureDescription window = graph.WindowTarget.IsNone ? default : graph.GetDescription(graph.WindowTarget);
            picture = window.Width > 0 ? new Size(window.Width, window.Height) : new Size(DefaultWidth, DefaultHeight);
        }

        Result<GpuBuffer> constants = graph.Buffer(new("World constants", 1024));
        Result<LightBuffers> lights = _lightTiles.Prepare(
            graph,
            inputs.PointLights,
            inputs.SpotLights,
            inputs.RectLights
        );
        Result asked = Result.FindFailure(effects, tables, constants, lights);
        if (!asked.Ok)
        {
            Fail(graph, RenderFailure.Resource, asked.Error);

            return null;
        }

        // One readback buffer, so one screenshot a frame: the first asked for, of the whole picture at 8 bits a
        // channel, which each view of the window is copied into. The request stays until the shading's step of the
        // last view reads the picture back, so a frame that cannot draw loses none.
        Screenshot? screenshot = null;
        if (_screenshots.Reader.TryPeek(out ScreenshotRequested saved))
        {
            Result<GpuBuffer> readback = graph.Buffer(
                new("Output readback", (long)picture.Width * picture.Height * 4, BufferUsages.Readback)
            );
            Result<GpuTexture> copy = graph.Texture(
                new(
                    "Screenshot copy",
                    picture.Width,
                    picture.Height,
                    GpuFormat.R8G8B8A8Unorm,
                    TextureUsages.CopyDestination | TextureUsages.CopySource
                )
            );
            if (Result.FindFailure(readback, copy) is { Ok: false } failed)
            {
                Log.Error($"The screenshot was not taken: {failed.Error}");
                _screenshots.Reader.TryRead(out _);
            }
            else
                screenshot = new Screenshot(saved.Path, copy.Value, readback.Value);
        }

        GpuWorld world = new()
        {
            Frame = constants.Value,
            Instances = tables.Value.Rows,
            InstanceCount = _instances.Count,
            InstanceCapacity = _instances.Capacity,
            Materials = tables.Value.Materials,
            Failures = tables.Value.Failures,
            Vertices = _resources.VertexPool,
            Indices = _resources.IndexPool,
            Meshes = _resources.Meshes,
            Models = _resources.Models,
            MaterialShaders = _resources.MaterialShaders,
            HeapTextures = _resources.GetHeapTextures(),
            PreparedDomains = _preparedDomains,
            Inputs = inputs,
            Picture = picture,
            Lights = lights.Value,
            Effects = effects.Value.Effects,
            EffectRows = effects.Value.Rows,
            Screenshot = screenshot,
        };

        // The world's buffer holds the models' row alone, where a view's holds it: what a pass that reads the world's
        // tables through no view takes.
        if (!graph.Upload(world.Frame, [world.ModelsRow], GpuModels.Offset))
        {
            Fail(graph, RenderFailure.Resource, "the world's constants were not staged.");

            return null;
        }

        // A view for each camera that draws into a render texture, then one for each that draws into the window, or
        // one for the window when none does.
        int windows = 0;
        for (int i = 0; i < inputs.Cameras.Count; i++)
        {
            if (inputs.Cameras.Second[i].Target.IsWindow)
                windows++;
        }

        int views = inputs.Cameras.Count - windows + Math.Max(1, windows);

        return (world, _instances.Instances, new RenderViews(views));
    }

    /// <summary>
    /// The view of this turn on the GPU: its camera's targets at its size, the frame's rows in its own buffer and its
    /// tiles' lights; none while the render texture its camera draws into is not made.
    /// </summary>
    public GpuView? Render(in Frame frame, RenderGraph graph, in ViewInput input)
    {
        GpuWorld world = input.World;
        if (world.Inputs is not { } inputs)
            return null;

        float renderScale = _settings.RenderScale;
        float fieldOfView = _settings.FieldOfView;

        // The view's camera, what it draws into, and where the view is in the whole picture: a section of the window,
        // at the field of view of the settings when that is more than 0, or all of a render texture.
        (Transform Transform, Camera Camera) camera = GetCamera(inputs.Cameras, graph.View);
        CameraTarget into = camera.Camera.Target;
        GpuTexture target = graph.WindowTarget;
        Rectangle shown = GetSection(into.Section, world.Picture);
        Rectangle part = default;
        if (!into.IsWindow)
        {
            target = _resources.Get(into.Texture);
            if (target.IsNone)
                return null;

            TextureDescription texture = graph.GetDescription(target);
            shown = new Rectangle(0, 0, texture.Width, texture.Height);
        }
        else
        {
            if (!target.IsNone)
            {
                TextureDescription window = graph.GetDescription(target);
                part = GetSection(into.Section, new Size(window.Width, window.Height));
            }

            if (fieldOfView > 0)
                camera.Camera = camera.Camera with { FieldOfView = fieldOfView };
        }

        // The world drawn at the render scale of the size the view is shown at, or when that is auto at the most that
        // fits 1920 by 1080, never more than all of it.
        float scale = MathF.Min(
            1,
            renderScale > 0
                ? renderScale
                : MathF.Min((float)AutoRenderWidth / shown.Width, (float)AutoRenderHeight / shown.Height)
        );
        int width = Math.Max(1, (int)MathF.Round(shown.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(shown.Height * scale));

        // The frame's buffer, the view's targets and its tiles asked for in that order; the glass
        // targets only while a material draws translucent, so the graph frees them in a world with none.
        bool hasGlass = _resources.HasTranslucent;
        TextureDescription depthTarget = new(
            "Depth",
            width,
            height,
            GpuFormat.D32Sfloat,
            TextureUsages.DepthTarget | TextureUsages.Sampled
        );
        Result<GpuBuffer> constants = graph.Buffer(new("Frame constants", 1024));
        Result<GpuTexture> depth = graph.Texture(depthTarget);
        Result<GpuTexture> glassDepth = GetGlass(depthTarget with { Name = "Glass depth" });
        Result<GpuTexture> glassBase = GetGlass(GetTarget("Glass base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> glassNormal = GetGlass(GetTarget("Glass normal", GpuFormat.R16G16Unorm));
        Result<GpuTexture> glassSurface = GetGlass(GetTarget("Glass surface", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> normal = graph.Texture(GetTarget("GBuffer normal", GpuFormat.R16G16Unorm));
        Result<GpuTexture> baseColor = graph.Texture(GetTarget("GBuffer base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> surface = graph.Texture(GetTarget("GBuffer surface", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> velocity = graph.Texture(GetTarget("Velocity", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> hdr = graph.Texture(
            new(
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
            new(
                "Output",
                width,
                height,
                GpuFormat.R8G8B8A8Unorm,
                TextureUsages.Storage | TextureUsages.ColorTarget | TextureUsages.Sampled | TextureUsages.CopySource
            )
        );
        Result<GpuBuffer> tiles = LightTiles.PrepareTiles(graph, width, height);
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
            tiles
        );
        if (!asked.Ok)
        {
            Fail(graph, RenderFailure.Resource, asked.Error);

            return null;
        }

        // What the view keeps from frame to frame, by its number.
        while (_views.Count <= graph.View)
        {
            _views.Add((new FrameView(), new PixelEffects()));
        }

        FrameView frameView = _views[graph.View].Frame;
        GpuView view = new()
        {
            Frame = constants.Value,
            Width = width,
            Height = height,
            ShownPart = shown,
            Target = target,
            TargetPart = part,
            RenderTexture = into.Texture,
            HasHistory = frameView.HasHistory(width, height),
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
            Instances = world.Instances,
            InstanceCount = world.InstanceCount,
            InstanceCapacity = world.InstanceCapacity,
            Materials = world.Materials,
            Failures = world.Failures,
            Vertices = world.Vertices,
            Indices = world.Indices,
            Meshes = world.Meshes,
            Models = world.Models,
            MaterialShaders = world.MaterialShaders,
            HeapTextures = world.HeapTextures,
            LightTable = world.Lights.Table,
            LightTiles = tiles.Value,
            Effects = world.Effects,
            EffectRows = world.EffectRows,
            PreparedDomains = world.PreparedDomains,
        };

        // The camera jitters while one of the effects asks for it, or while a gem upscales.
        bool isJittered = input.Upscaling is not null;
        foreach (GpuEffect effect in view.Effects.Span)
        {
            isJittered |= effect.State == GpuState.Ready && effect.Needs.HasJitter;
        }

        Result uploaded = frameView.Upload(
            graph,
            view,
            world,
            camera,
            frame,
            isJittered ? FrameView.GetJitter(frame.Index) : Vector2.Zero,
            GetSun(inputs.DirectionalLights),
            _settings.SkyBrightness,
            input.Traced is { } traced
                ? new GpuTracedWorld { Acceleration = traced.Structure.Id, HitMaterials = traced.HitMaterials.Id }
                : default
        );
        if (!uploaded.Ok)
        {
            Fail(graph, RenderFailure.Resource, uploaded.Error);

            return null;
        }

        // Each tile's lights.
        _lightTiles.Record(graph, view);

        return view;

        TextureDescription GetTarget(string name, GpuFormat format) => new(name, width, height, format);

        Result<GpuTexture> GetGlass(in TextureDescription glass) =>
            hasGlass ? graph.Texture(glass) : Result.Success(GpuTexture.None);
    }

    /// <summary>
    /// Records the view's passes, each in its stage, from the view the view's step made.
    /// </summary>
    public FinalImage? Render(in Frame frame, RenderGraph graph, in ShadingInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each; while one failed, the shading
        // draws nothing, and the banner tells it.
        GpuState shaders = graph.GetState(_shadingShaders.AsSpan());
        if (shaders == GpuState.Failed)
            graph.Fail(RenderFailure.Shader, "Surfaces are not shaded", "a shader of the shading was not built.");

        if (shaders != GpuState.Ready)
            return null;

        GpuView gpu = input.View;
        DebugView renderView = _settings.RenderView;
        TextureFilter upscaleFilter = _settings.UpscaleFilter;

        ReadOnlySpan<GpuResource> indirect = input.Light is { } light
            ? [light.Diffuse, light.Reflections, light.Probes, light.ProbeDepth]
            : [];
        ReadOnlySpan<GpuResource> sky = input.Sky is { } tables ? [tables.Transmittance, tables.View] : [];

        // Every pixel's surface shaded from the sky's tables, its tile's lights, the shadows where there are any and
        // the indirect light, or the sky where nothing traced it, or the failure of the sky or its clouds where they
        // failed.
        ComputePass shade = graph
            .Compute(
                "Shade surfaces",
                _shadeShader,
                new GpuConstants(gpu.Frame.Id, GetSkyFailures(graph.Failures)),
                RenderStages.Shade
            )
            .Reads(sky)
            .Reads(
                gpu.Frame,
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

        // The velocity where a failure of the sky or its clouds draws, which does not move on the screen.
        shade.Writes(gpu.Hdr, gpu.Velocity).DispatchOver(gpu.Width, gpu.Height);

        // The nearest pane over the shaded picture, from what the resolve left in the glass targets, with its rays
        // through the traced world where there is one, and the air in front of it or of the opaque surface; nothing to
        // do with neither glass nor air.
        if (!gpu.GlassDepth.IsNone || input.Air is not null)
        {
            ComputePass translucent = graph
                .Compute("Shade translucent", _translucentShader, new GpuConstants(gpu.Frame.Id), RenderStages.Shade)
                .Reads(sky)
                .Reads(gpu.Frame, gpu.Depth, gpu.GlassDepth, gpu.GlassBase, gpu.GlassNormal, gpu.GlassSurface)
                .Reads(indirect);
            if (input.Traced is { } traced)
                translucent.Reads(traced.HitMaterials, gpu.Meshes, gpu.Instances);

            if (input.Air is { } air)
                translucent.Reads(air.Volume);

            translucent.Writes(gpu.Hdr).DispatchOver(gpu.Width, gpu.Height);
        }

        // A debug view of light draws it into the picture in place of the lit one, black where none was traced, so the
        // effects finish it as they do the picture.
        if (renderView is DebugView.IndirectDiffuse or DebugView.Reflections)
        {
            graph
                .Compute(
                    "Show light",
                    _lightViewShader,
                    new GpuConstants(gpu.Frame.Id, (uint)renderView),
                    RenderStages.Shade
                )
                .Reads(gpu.Frame, gpu.Depth, gpu.GBufferNormal)
                .Reads(indirect)
                .Writes(gpu.Hdr)
                .DispatchOver(gpu.Width, gpu.Height);
        }

        _views[graph.View].Effects.Record(graph, gpu, frame.Index);

        // The output drawn as the debug view asks, a Depth view black from its far distance, and the picture's middle
        // tones shown at the gamma.
        graph
            .Compute(
                "Show surface",
                _showShader,
                new GpuConstants(
                    gpu.Frame.Id,
                    (uint)renderView,
                    BitConverter.SingleToUInt32Bits(_settings.DepthViewFar),
                    BitConverter.SingleToUInt32Bits(Math.Clamp(_settings.Gamma, 0.5f, 2f))
                ),
                RenderStages.Post
            )
            .Reads(gpu.Frame, gpu.Depth, gpu.GBufferNormal, gpu.GBufferBase, gpu.GBufferSurface, gpu.Velocity, gpu.Hdr)
            .Writes(gpu.Output)
            .DispatchOver(gpu.Width, gpu.Height);

        // What the view's target and a screenshot show: the output as a gem upscaled it to the size it is shown at, or
        // the output itself, stretched over the view's part of the target with the upscale filter.
        GpuTexture upscaled = input.Upscaling is { } upscaling ? upscaling.Record(graph, gpu) : GpuTexture.None;
        GpuTexture shown = upscaled.IsNone ? gpu.Output : upscaled;
        if (gpu.Target != graph.WindowTarget)
        {
            // A render texture is sRGB, so a material reads linear colour from it. The picture is encoded for the
            // screen already: it is stretched into a texture of the same size, then copied with its bytes unchanged.
            TextureDescription target = graph.GetDescription(gpu.Target);
            Result<GpuTexture> picture = graph.Texture(
                new(
                    "Render texture picture",
                    target.Width,
                    target.Height,
                    GpuFormat.R8G8B8A8Unorm,
                    TextureUsages.CopyDestination | TextureUsages.CopySource
                )
            );
            if (picture.Ok)
            {
                graph.Blit(shown, picture.Value, RenderStages.Post, upscaleFilter);
                graph.Copy(picture.Value, gpu.Target, RenderStages.Post);
            }
            else
                graph.Fail(RenderFailure.Resource, "Render texture is not drawn", picture.Error);
        }
        else if (!gpu.Target.IsNone)
            graph.Blit(shown, gpu.Target, gpu.TargetPart, RenderStages.Post, upscaleFilter);

        // The screenshot asked for: each view of the window copied into the whole picture at its part, stretched to it
        // with the upscale filter, and the picture read back after the last view, which is one of the window; written
        // once the GPU is done with it.
        if (input.World.Screenshot is { } shot && gpu.Target == graph.WindowTarget)
        {
            graph.Blit(shown, shot.Copy, gpu.ShownPart, RenderStages.Post, upscaleFilter);
            if (graph.View == input.Views.Count - 1)
            {
                Size picture = input.World.Picture;
                _screenshots.Reader.TryRead(out _);
                graph.ReadBack(shot.Copy, shot.Readback, RenderStages.Post);
                SaveScreenshotAsync(shot.Readback, shot.Path, picture.Width, picture.Height).FireAndForget();
            }
        }

        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }

        return new(shown, gpu.ShownWidth, gpu.ShownHeight, gpu.RenderTexture);
    }

    /// <summary>
    /// Numbers the world anew, so the world's step forgets each view's history at the first frame of what comes.
    /// </summary>
    /// <remarks>
    /// It runs on the game thread and does not leave it, so the world goes on to what comes before another frame is
    /// published.
    /// </remarks>
    public Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        _worldNumber++;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Holds the domain loading until what <paramref name="entities"/> draw is on the GPU and the shaders are built.
    /// </summary>
    /// <remarks>
    /// It asks for every model and material the entities draw, and the effects of their first post processing, or the
    /// default's. It waits until they are on the GPU, or failed and drawn as the failure or left out, and until the
    /// shading's shaders are built or failed. Materials are asked for at once; models once the pools are
    /// made, and the effects, by the world's step. One that comes while another waits ends that one. Render thread,
    /// whose tables and counts these are.
    /// </remarks>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        EndPreparing();
        Ecs.Components.Renderer[] renderers =
        [
            .. entities
                .SelectMany(entity => entity.OfType<Ecs.Components.Renderer>())
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

    /// <summary>
    /// Copies what the frame draws out of the entities and publishes it for the world's step, and hands that step the
    /// assets read again and the screenshots asked for.
    /// </summary>
    internal void PublishInputs(in Frame frame)
    {
        RenderInputs back = _inputs.Back;
        back.Renderers.Fill(_ecs);
        back.Cameras.Fill(_ecs);
        back.DirectionalLights.Fill(_ecs);
        back.PointLights.Fill(_ecs);
        back.SpotLights.Fill(_ecs);
        back.RectLights.Fill(_ecs);
        back.PostProcessing.Fill(_ecs);
        back.WorldNumber = _worldNumber;
        _inputs.Publish();

        foreach (AssetReloaded reloaded in frame.Events.Get<AssetReloaded>())
        {
            _reloaded.Writer.TryWrite(reloaded.Id);
        }

        foreach (ScreenshotRequested asked in frame.Events.Get<ScreenshotRequested>())
        {
            _screenshots.Writer.TryWrite(asked);
        }
    }

    /// <summary>
    /// The camera of view <paramref name="view"/>. Those that draw into a render texture come first, in the entities'
    /// order, so a material that samples one in a view of the window shows this frame's picture. Then come those that
    /// draw into all of the window, then those that draw into a part of it, which so show over the others. One at the
    /// origin draws into the window when no camera does.
    /// </summary>
    private static (Transform Transform, Camera Camera) GetCamera(EcsSnapshot<Transform, Camera> cameras, int view)
    {
        int place = view;
        for (int turn = 0; turn < 3; turn++)
        {
            for (int i = 0; i < cameras.Count; i++)
            {
                CameraTarget target = cameras.Second[i].Target;
                int cameraTurn =
                    !target.IsWindow ? 0
                    : target.Section == ScreenSection.Full ? 1
                    : 2;
                if (cameraTurn == turn && place-- == 0)
                    return (cameras.First[i], cameras.Second[i]);
            }
        }

        return (new Transform(), new Camera());
    }

    /// <summary>
    /// The part of a picture of <paramref name="size"/> that <paramref name="section"/> is, in pixels from its top
    /// left; the middle pixel of an odd side goes to the right and the bottom parts.
    /// </summary>
    private static Rectangle GetSection(ScreenSection section, Size size)
    {
        int left = size.Width / 2;
        int top = size.Height / 2;

        return section switch
        {
            ScreenSection.Full => new(0, 0, size.Width, size.Height),
            ScreenSection.TopLeft => new(0, 0, left, top),
            ScreenSection.TopRight => new(left, 0, size.Width - left, top),
            ScreenSection.BottomLeft => new(0, top, left, size.Height - top),
            ScreenSection.BottomRight => new(left, top, size.Width - left, size.Height - top),
            ScreenSection.Top => new(0, 0, size.Width, top),
            ScreenSection.Bottom => new(0, top, size.Width, size.Height - top),
            ScreenSection.Left => new(0, 0, left, size.Height),
            ScreenSection.Right => new(left, 0, size.Width - left, size.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "There is no such section."),
        };
    }

    /// <summary>
    /// Gets the failure bits of <c>ShadeSurfaces.slang</c>'s pass header: 1 while the sky failed, 2 while its clouds
    /// failed.
    /// </summary>
    private static uint GetSkyFailures(ReadOnlySpan<(RenderFailure Kind, string What)> failures)
    {
        uint bits = 0;
        foreach ((RenderFailure kind, _) in failures)
        {
            if (kind == RenderFailure.Sky)
                bits |= 1;
            else if (kind == RenderFailure.Weather)
                bits |= 2;
        }

        return bits;
    }

    /// <summary>
    /// Gets the directional light the frame is lit by.
    /// </summary>
    /// <remarks>
    /// The sun while it is up, else the moon while it is up, else the first there is, else a white light from above.
    /// </remarks>
    /// <returns>
    /// The unit direction towards the light, and its colour times its intensity, in lux, above the air.
    /// </returns>
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
    /// Lets the loading domain show when the world's or the view's step is out, and draws the frame's render failures
    /// over the window; nothing when none failed.
    /// </summary>
    void IRender.Render(in Frame frame, RenderGraph graph)
    {
        // A step that threw never reaches its own release; while it renders, it releases when it is ready, so
        // nothing pops in.
        if (graph.IsMakerOut<GpuWorld>() || graph.IsMakerOut<GpuView>())
            EndPreparing();

        _banner.Record(graph, frame);
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
    /// Tells the graph that the world or a view is not made in this frame, and lets the loading domain show without
    /// it.
    /// </summary>
    private void Fail(RenderGraph graph, RenderFailure kind, string reason)
    {
        graph.Fail(kind, "View is not made", reason);
        EndPreparing();
    }

    /// <summary>
    /// Writes what was read back to <paramref name="path"/> once the GPU is done with it, opaque as the window shows
    /// it.
    /// </summary>
    /// <remarks>
    /// The gem that writes textures writes the file.
    /// </remarks>
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
