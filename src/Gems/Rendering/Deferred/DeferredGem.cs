using System.Collections.Immutable;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Input;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.Deferred;

/// <summary>
/// What the deferred renderer takes each frame: the world as the GPU holds it, and, where a gem makes them, the world
/// as rays see it, each pixel's shadows, the light that reaches the surfaces indirectly, the air between the camera and
/// them, and the output's upscaling.
/// </summary>
public readonly record struct DeferredInput(
    GpuView View,
    TracedWorld? Traced,
    ShadowMask? Shadows,
    IndirectLight? Light,
    Air? Air,
    Upscaling? Upscaling
);

/// <summary>
/// Lights the world the GPU holds, deferred: each frame, from the view the world's gem made and the GBuffer a geometry
/// gem drew into it, the shading, shadowed by the frame's shadow mask where there is one, the nearest glass over it, the
/// effects, a debug view where one is asked for, and the output into the window and any screenshot asked for. Where
/// nothing made the world as rays see it, the shadows, the indirect light or the air, their rows of the frame's buffer
/// are 0.
/// As a domain loads, it holds the loading screen until its passes are built. It is public because nothing in its
/// assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class DeferredGem
    : IGem,
        IHasSettings<DeferredSettings>,
        IRenderFrom<DeferredInput>,
        IHookIntoDomainLoading
{
    private readonly IGraphicsApi _gpu;
    private readonly AssetManager _assets;
    private readonly EventCoordinator _events;
    private readonly Threads _threads;
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
    /// Every shader the passes run, which the GPU provider builds before the frame is drawn.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shaders;
    private bool _hasDrawn;

    /// <summary>
    /// How many domains have loaded since the start, and what the newest waits on before it shows: the gem's passes
    /// built, once the view counts it prepared. Render thread.
    /// </summary>
    private int _domainsLoaded;
    private TaskCompletionSource? _loading;

    public DeferredGem(
        DeferredSettings settings,
        IGraphicsApi gpu,
        AssetManager assets,
        Threads threads,
        EventCoordinator events,
        IDebugUI? debugUI = null,
        IInput? input = null
    )
    {
        Settings = settings;
        _debugWindow = debugUI is null ? null : new RendererDebugWindow(debugUI, input);
        _gpu = gpu;
        _assets = assets;
        _events = events;
        _threads = threads;
        _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
        {
            TypeArguments = [PassShaders.GetAtmosphere(assets)],
        };
        _translucentShader = PassShaders.GetEntry(assets, "ShadeTranslucent", "ShadeTranslucent") with
        {
            TypeArguments = [PassShaders.GetAtmosphere(assets)],
        };
        _lightShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowLight");
        _showShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowSurface");
        _shaders = [_shadeShader, _translucentShader, _lightShader, _showShader];
    }

    /// <summary>
    /// What the renderer shows, which the debug window also sets on the game thread. <see cref="Render"/> reads it on
    /// the render thread: a record, written whole, which no one changes.
    /// </summary>
    public DeferredSettings Settings { get; set; }

    /// <summary>
    /// Draws the debug window, which sets the settings.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (_debugWindow is not null)
            Settings = _debugWindow.Update(Settings);
    }

    /// <summary>
    /// Hands <see cref="Render"/> the screenshots asked for.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        foreach (ScreenshotRequested asked in frame.Events.Get<ScreenshotRequested>())
        {
            _screenshots.Writer.TryWrite(asked);
        }
    }

    /// <summary>
    /// Records the frame's passes, each in its stage, from the view the world's gem made.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in DeferredInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each. While one failed, the renderer draws
        // nothing and holds no domain back; else the loading domain shows once its models and materials are on the GPU.
        GpuState shaders = graph.GetState(_shaders.AsSpan());
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

        DeferredSettings settings = Settings;

        // The rows of what no gem made this frame are 0, so the shaders that read them trace, shadow and light nothing
        // indirectly, the sky lights the surfaces, and the air changes nothing.
        if (input.Traced is null)
            graph.Upload(gpu.Frame, [default(GpuTracedWorld)], GpuTracedWorld.Offset);

        if (input.Shadows is null)
            graph.Upload(gpu.Frame, [default(GpuShadows)], GpuShadows.Offset);

        if (input.Light is null)
            graph.Upload(gpu.Frame, [default(GpuIndirectLight)], GpuIndirectLight.Offset);

        if (input.Air is null)
            graph.Upload(gpu.Frame, [default(GpuAir)], GpuAir.Offset);

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
    /// Holds the domain loading until the gem's passes are built, once the world's view counts what it draws on the
    /// GPU. One that comes while another waits ends that one. On the render thread, whose count this is.
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
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = loading;
        await loading.Task.WaitAsync(cancel).ConfigureAwait(false);
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
}
