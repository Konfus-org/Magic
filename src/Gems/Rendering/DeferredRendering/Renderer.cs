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
/// The renderer, in three steps a frame. The view's step keeps the world on the GPU for the gems that draw it:
/// <see cref="PublishInputs"/> copies what the frame draws out of the entities and publishes it; on the render thread the step
/// takes the newest published, puts the models, materials, textures, instances, lights and sky's tables on the GPU,
/// uploads the frame's rows, and makes the frame's <see cref="GpuView"/> and its <see cref="GpuInstances"/>. The
/// shading's step lights that world, deferred: from the view and the GBuffer a geometry gem drew into it, the shading,
/// shadowed by the frame's shadow mask where there is one, the nearest glass over it, the effects, a debug view where
/// one is asked for, and the output into the window and any screenshot asked for. The banner's step draws the frame's
/// render failures over the top of the window, even when the view was not made. Where nothing made the world as rays
/// see it, the shadows, the indirect light or the air, their rows of the frame's buffer are 0. As a domain replaces the
/// world, it forgets the view's history and holds the loading screen until what comes is on the GPU and the shading's
/// shaders are built.
/// </summary>
public sealed class Renderer
    : IRender,
        IRender<ViewInput, (GpuView, GpuInstances)>,
        IRenderFrom<ShadingInput>,
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

    private readonly FrameView _frameView;

    private readonly SkyLuts _sky;

    private readonly LightTiles _lightTiles;

    private readonly Effects _effects;

    /// <summary>
    /// Every shader the view's passes run but the sky's, which the GPU provider builds before the frame is made.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _viewShaders;

    /// <summary>
    /// The shaders that fill the sky's tables; while one failed, the frame is drawn with no sky.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _skyShaders;

    private readonly ShaderEntry _shadeShader;

    private readonly ShaderEntry _translucentShader;

    private readonly ShaderEntry _lightViewShader;

    private readonly ShaderEntry _showShader;

    /// <summary>
    /// Every shader the shading's passes run, which the GPU provider builds before the step draws.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shadingShaders;

    private readonly PixelEffects _pixelEffects = new();

    private readonly FailureBanner _banner;

    private readonly IGraphicsApi _gpu;

    private readonly EventCoordinator _events;

    /// <summary>
    /// The screenshots asked for on the game thread, which the shading's step takes on the render thread.
    /// </summary>
    private readonly Channel<ScreenshotRequested> _screenshots = Channel.CreateUnbounded<ScreenshotRequested>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// The ids of the assets read again, heard on the game thread, which the view's step lets go of on the render thread.
    /// </summary>
    private readonly Channel<ulong> _reloaded = Channel.CreateUnbounded<ulong>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    private readonly Subscription _preprocessing;

    /// <summary>
    /// The number of the world the frames <see cref="PublishInputs"/> publishes are of, a new one each time a domain
    /// replaces it, so the view's step forgets the view's history at the first frame of what comes. Game thread.
    /// </summary>
    private int _worldNumber;

    /// <summary>
    /// The number of the world the view's step drew last, none before the first frame. Render thread.
    /// </summary>
    private int? _drawnWorldNumber;

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

    private bool _hasDrawn;

    public Renderer(
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
        _frameView = new FrameView();
        _sky = new SkyLuts(assets);
        _lightTiles = new LightTiles(assets);
        _effects = new Effects(assets, _resources);
        _banner = new FailureBanner(assets);
        _viewShaders = _lightTiles.Shaders;
        _skyShaders = _sky.Shaders;

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
    /// The newest inputs published, or the last again when none are newer, on the GPU, and the frame's view of them and
    /// its instances; none while the shaders build, the pools are not made, or nothing was published yet.
    /// </summary>
    public (GpuView, GpuInstances)? Render(in Frame frame, RenderGraph graph, in ViewInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState shaders = graph.GetState(_viewShaders.AsSpan());
        if (shaders == GpuState.Failed)
            Fail(graph, RenderFailure.Shader, "a shader of the view was not built.");

        if (shaders != GpuState.Ready)
            return null;

        // What was made from an asset read again is let go, so the next ask loads it anew and its failure is found again.
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

        DeferredRenderingSettings settings = _settings;
        Size resolution = settings.Resolution;
        float renderScale = settings.RenderScale;
        float fieldOfView = settings.FieldOfView;

        // The first frame of another world forgets last frame's camera and size, so nothing drawn before carries
        // into it.
        if (inputs.WorldNumber != _drawnWorldNumber)
        {
            _frameView.ClearHistory();
            _drawnWorldNumber = inputs.WorldNumber;
        }

        // Entities -> resources -> rows: the renderers written since the inputs before ask for their models and have
        // their rows built again, and the materials' rows are made again at upload.
        _instances.Fill(inputs.Renderers, frame.Index);

        // The loading domain shows once the models, materials and effects it waits for are on the GPU, or failed: a
        // model drawn as the failure cube, which is then waited for too, or, for an effect, left out; and once the
        // shading's and the sky's shaders are built, or failed. Its models are asked for here, as the pools only take them once
        // they are made; an ask for one asked for already does nothing.
        if (_preparing is { } preparing)
        {
            bool isPrepared =
                !_effects.IsLoading(graph, preparing.Effects)
                && graph.GetState(_shadingShaders.AsSpan()) != GpuState.Loading
                && graph.GetState(_skyShaders.AsSpan()) != GpuState.Loading;
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
        _resources.EvictUnusedModels(inputs.Renderers.Second[.._instances.Count], frameStart, settings.ModelKeepSec);
        _resources.Upload(graph);

        // The picture's effects: the first post processing's, or the engine's, exposed by the exposure compensation;
        // each whose material the settings turn off left out, which most frames name none of.
        MaterialSlots post;
        if (inputs.PostProcessing.Count > 0)
            post = inputs.PostProcessing.First[0].Effects;
        else
        {
            post = _defaultPost.Effects;
            post[0].Overrides["compensation"] = settings.ExposureCompensation;
        }

        ImmutableArray<string> disabled = settings.DisabledEffects;
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

        // The finished picture at the resolution, or at the window's size when it has no side, and 1280 by 720 with no
        // window; the world drawn at the render scale of it, or when that is auto at the most that fits 1920 by 1080,
        // never more than all of it.
        Size shown = resolution;
        if (shown.Width <= 0 || shown.Height <= 0)
        {
            TextureDescription window = graph.WindowTarget.IsNone ? default : graph.GetDescription(graph.WindowTarget);
            shown = window.Width > 0 ? new Size(window.Width, window.Height) : new Size(DefaultWidth, DefaultHeight);
        }

        float scale = MathF.Min(
            1,
            renderScale > 0
                ? renderScale
                : MathF.Min((float)AutoRenderWidth / shown.Width, (float)AutoRenderHeight / shown.Height)
        );
        int width = Math.Max(1, (int)MathF.Round(shown.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(shown.Height * scale));

        // The frame's buffer, the view's targets, the light table and its tiles, and the sky's tables asked for in that
        // order; the glass targets only while a material draws translucent, so the graph frees them in a world with
        // none.
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
            effects,
            tables,
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
        {
            Fail(graph, RenderFailure.Resource, asked.Error);

            return null;
        }

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
            LightTable = lights.Value.Table,
            LightTiles = lights.Value.Tiles,
            SkyTransmittance = transmittance.Value,
            SkyMultiScattering = multiScattering.Value,
            SkyView = skyView.Value,
            Effects = effects.Value.Effects,
            EffectRows = effects.Value.Rows,
            PreparedDomains = _preparedDomains,
        };

        // The first camera that draws into the window, else one at the origin, at the field of view of the settings
        // when that is more than 0.
        (Transform Transform, Camera Camera) camera = (new Transform(), new Camera());
        for (int i = 0; i < inputs.Cameras.Count; i++)
        {
            if (!inputs.Cameras.Second[i].Target.IsWindow)
                continue;

            camera = (inputs.Cameras.First[i], inputs.Cameras.Second[i]);
            break;
        }

        if (fieldOfView > 0)
            camera.Camera = camera.Camera with { FieldOfView = fieldOfView };

        // The camera jitters while one of the effects asks for it, or while a gem upscales.
        bool isJittered = input.Upscaling is not null;
        foreach (GpuEffect effect in view.Effects.Span)
        {
            isJittered |= effect.State == GpuState.Ready && effect.Needs.HasJitter;
        }

        // The sky while there is one and its shaders are built.
        GpuState skyShaders = graph.GetState(_skyShaders.AsSpan());
        if (inputs.Skies.Count > 0 && skyShaders == GpuState.Failed)
            graph.Fail(RenderFailure.Sky, "Sky is not drawn", "a shader of the sky was not built.");

        bool hasSky = inputs.Skies.Count > 0 && skyShaders == GpuState.Ready;
        Result uploaded = _frameView.Upload(
            graph,
            view,
            lights.Value.Count,
            camera,
            frame,
            isJittered ? FrameView.GetJitter(frame.Index) : Vector2.Zero,
            GetSun(inputs.DirectionalLights),
            hasSky,
            settings.SkyBrightness
        );
        if (!uploaded.Ok)
        {
            Fail(graph, RenderFailure.Resource, uploaded.Error);

            return null;
        }

        // The sky's tables while there is a sky, and each tile's lights.
        if (hasSky)
            _sky.Record(graph, view);

        _lightTiles.Record(graph, view);
        return (view, _instances.Instances);

        TextureDescription GetTarget(string name, GpuFormat format) => new(name, width, height, format);

        static TextureDescription GetSkyTable(string name, int width, int height) =>
            new(name, width, height, GpuFormat.R16G16B16A16Sfloat);

        Result<GpuTexture> GetGlass(in TextureDescription glass) =>
            hasGlass ? graph.Texture(glass) : Result.Success(GpuTexture.None);
    }

    /// <summary>
    /// Records the frame's passes, each in its stage, from the view the view's step made.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in ShadingInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each; while one failed, the renderer
        // draws nothing.
        GpuState shaders = graph.GetState(_shadingShaders.AsSpan());
        if (shaders == GpuState.Failed)
            graph.Fail(RenderFailure.Shader, "Surfaces are not shaded", "a shader of the shading was not built.");

        if (shaders != GpuState.Ready)
            return;

        GpuView gpu = input.View;
        DeferredRenderingSettings settings = _settings;
        DebugView renderView = settings.RenderView;
        TextureFilter upscaleFilter = settings.UpscaleFilter;

        ReadOnlySpan<GpuResource> indirect = input.Light is { } light
            ? [light.Diffuse, light.Reflections, light.Probes, light.ProbeDepth]
            : [];

        // Every pixel's surface shaded from the sky's tables, its tile's lights, the shadows where there are any and the
        // indirect light, or the sky where nothing traced it, or the failure of the sky or its clouds where they failed.
        ComputePass shade = graph
            .Compute(
                "Shade surfaces",
                _shadeShader,
                new GpuConstants(gpu.Frame.Id, GetSkyFailures(graph.Failures)),
                RenderStages.Shade
            )
            .Reads(
                gpu.Frame,
                gpu.SkyTransmittance,
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

        // The velocity where a failure of the sky or its clouds draws, which does not move on the screen.
        shade.Writes(gpu.Hdr, gpu.Velocity).DispatchOver(gpu.Width, gpu.Height);

        // The nearest pane over the shaded picture, from what the resolve left in the glass targets, with its rays
        // through the traced world where there is one, and the air in front of it or of the opaque surface; nothing to
        // do with neither glass nor air.
        if (!gpu.GlassDepth.IsNone || input.Air is not null)
        {
            ComputePass translucent = graph
                .Compute("Shade translucent", _translucentShader, new GpuConstants(gpu.Frame.Id), RenderStages.Shade)
                .Reads(
                    gpu.Frame,
                    gpu.SkyTransmittance,
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

        _pixelEffects.Record(graph, gpu, frame.Index);

        // The output drawn as the debug view asks, a Depth view black from its far distance, and the picture's middle
        // tones shown at the gamma.
        graph
            .Compute(
                "Show surface",
                _showShader,
                new GpuConstants(
                    gpu.Frame.Id,
                    (uint)renderView,
                    BitConverter.SingleToUInt32Bits(settings.DepthViewFar),
                    BitConverter.SingleToUInt32Bits(Math.Clamp(settings.Gamma, 0.5f, 2f))
                ),
                RenderStages.Post
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
            graph.Blit(shown, target, RenderStages.Post, upscaleFilter);

        // One readback buffer, so one screenshot a frame: the last asked for, read back at the size it is shown at, and
        // through a copy stretched to it with the upscale filter, of 8 bits a channel, where the shown picture is
        // another size or has more; written once the GPU is done with it.
        ScreenshotRequested? shot = null;
        while (_screenshots.Reader.TryRead(out ScreenshotRequested asked))
        {
            shot = asked;
        }

        if (shot is { } saved)
        {
            (int width, int height) = (gpu.ShownWidth, gpu.ShownHeight);
            TextureDescription drawn = graph.GetDescription(shown);
            Result<GpuBuffer> readback = graph.Buffer(
                new("Output readback", (long)width * height * 4, BufferUsages.Readback)
            );
            Result<GpuTexture> read =
                drawn.Format == GpuFormat.R8G8B8A8Unorm && drawn.Width == width && drawn.Height == height
                    ? Result.Success(shown)
                    : graph.Texture(
                        new(
                            "Screenshot copy",
                            width,
                            height,
                            GpuFormat.R8G8B8A8Unorm,
                            TextureUsages.CopyDestination | TextureUsages.CopySource
                        )
                    );
            if (Result.FindFailure(readback, read) is { Ok: false } failed)
                Log.Error($"The screenshot was not taken: {failed.Error}");
            else
            {
                if (read.Value != shown)
                    graph.Blit(shown, read.Value, RenderStages.Post, upscaleFilter);

                graph.ReadBack(read.Value, readback.Value, RenderStages.Post);
                SaveScreenshotAsync(readback.Value, saved.Path, width, height).FireAndForget();
            }
        }

        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }
    }

    /// <summary>
    /// Lets the loading domain show when the view's step is out, and draws the frame's render failures over the
    /// window; nothing when none failed.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph)
    {
        // A view step that threw never reaches its own release; while it renders, it releases when it is ready, so
        // nothing pops in.
        if (graph.IsMakerOut<GpuView>())
            EndPreparing();

        _banner.Record(graph, frame);
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
    /// Holds the domain loading until it can show: asks for every model and material <paramref name="entities"/> draw,
    /// and the effects of their first post processing, or the default's, and waits until they are on the GPU, or failed
    /// and drawn as the failure or left out, and until the shading's and the sky's shaders are built or failed.
    /// Materials are asked for at once; models once the pools are made, and the effects, by the view's step. One that
    /// comes while another waits ends that one. On the render thread, whose tables and counts these are.
    /// </summary>
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
    /// Copies what the frame draws out of the entities and publishes it for the view's step, hands the view's step the
    /// assets read again, and the shading's step the screenshots asked for.
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
        back.Skies.Fill(_ecs);
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
    /// The failure bits of <c>ShadeSurfaces.slang</c>'s pass header: 1 while the sky failed, 2 while its clouds failed.
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
    /// Tells the graph that the view is not made in this frame, and lets the loading domain show without it.
    /// </summary>
    private void Fail(RenderGraph graph, RenderFailure kind, string reason)
    {
        graph.Fail(kind, "View is not made", reason);
        EndPreparing();
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
