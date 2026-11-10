using System.Collections.Immutable;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Graphics;
using Magic.Input;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.Deferred;

/// <summary>
/// What the deferred renderer takes each frame: the world as the GPU holds it, and, where a gem makes them, the world
/// as rays see it, each pixel's shadows and the light that reaches the surfaces indirectly.
/// </summary>
public readonly record struct DeferredInput(
    GpuView View,
    TracedWorld? Traced,
    ShadowMask? Shadows,
    IndirectLight? Light
);

/// <summary>
/// The view as the world on the GPU posted it this frame, and the graph its passes record into.
/// </summary>
internal readonly record struct RenderView(GpuView Gpu, RenderGraph Graph);

/// <summary>
/// Lights the world the GPU holds, deferred: each frame, from the view the world's gem made and the GBuffer a geometry
/// gem drew into it, the shading, shadowed by the frame's shadow mask where there is one, the nearest glass over it, the
/// effects, a debug view where one is asked for, and the output into the window and any screenshot asked for. Where
/// nothing made the world as rays see it, the shadows or the indirect light, their rows of the frame's buffer are 0.
/// As a domain loads, it holds the loading screen until its passes are built. It is public because nothing in its
/// assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class DeferredGem : IGem, IRenderFrom<DeferredInput>, IHookIntoDomainLoading
{
    private readonly IGraphicsApi _gpu;
    private readonly AssetManager _assets;
    private readonly EventCoordinator _events;
    private readonly Threads _threads;
    private readonly Mailbox<RenderInputs> _inputs = new();
    private readonly Channel<ScreenshotRequested> _screenshots = Channel.CreateUnbounded<ScreenshotRequested>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly Lighting _lighting;
    private readonly PixelEffects _pixelEffects = new();
    private readonly SurfaceView _surfaceView;
    private readonly RendererDebugWindow? _debugWindow;

    /// <summary>
    /// Every shader the passes run, which the GPU provider builds before the frame is drawn.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shaders;

    private DebugView _debugView;

    /// <summary>
    /// The settings <see cref="Render"/> draws with, taken from <see cref="_inputs"/>, until newer are. Render thread.
    /// </summary>
    private RenderInputs? _current;
    private bool _hasDrawn;

    /// <summary>
    /// Whether the renderer draws: failed once a shader is not built. Render thread.
    /// </summary>
    private Result _started = Result.Success();

    /// <summary>
    /// How many domains have loaded since the start, and what the newest waits on before it shows: the gem's passes
    /// built, once the view counts it prepared. Render thread.
    /// </summary>
    private int _domainsLoaded;
    private TaskCompletionSource? _loading;

    public DeferredGem(
        IGraphicsApi gpu,
        AssetManager assets,
        Threads threads,
        EventCoordinator events,
        IDebugUI? debugUI = null,
        IInput? input = null
    )
    {
        _debugWindow = debugUI is null ? null : new RendererDebugWindow(debugUI, input);
        _gpu = gpu;
        _assets = assets;
        _events = events;
        _threads = threads;
        _lighting = new Lighting(assets);
        _surfaceView = new SurfaceView(assets);
        _shaders = [.. _lighting.Shaders, .. _surfaceView.Shaders];
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
    /// Publishes the settings for <see cref="Render"/>, after the screenshots asked for.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        RenderInputs back = _inputs.Back;
        back.DebugView = _debugView;
        foreach (ScreenshotRequested asked in frame.Events.Get<ScreenshotRequested>())
        {
            _screenshots.Writer.TryWrite(asked);
        }

        _inputs.Publish();
    }

    /// <summary>
    /// Records the frame's passes, each in its stage, from the view the world's gem made.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph, in DeferredInput input)
    {
        if (!_started.Ok)
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

        // The loading domain shows once its models and materials are on the GPU, now the gem's passes are built.
        GpuView gpu = input.View;
        if (_loading is { } loading && gpu.PreparedDomains >= _domainsLoaded)
        {
            loading.TrySetResult();
            _loading = null;
        }

        RenderView view = new(gpu, graph);

        // The rows of what no gem made this frame are 0, so the shaders that read them trace, shadow and light nothing
        // indirectly, and the sky lights the surfaces.
        if (input.Traced is null)
            graph.Upload(gpu.Frame, [default(GpuTracedWorld)], GpuTracedWorld.Offset);

        if (input.Shadows is null)
            graph.Upload(gpu.Frame, [default(GpuShadows)], GpuShadows.Offset);

        if (input.Light is null)
            graph.Upload(gpu.Frame, [default(GpuIndirectLight)], GpuIndirectLight.Offset);

        ReadOnlySpan<GpuResource> indirect = input.Light is { } light
            ? [light.Diffuse, light.Reflections, light.Probes, light.ProbeDepth]
            : [];

        // The passes, each in its stage, each reading what the ones before it wrote: the shading, lit by the frame's
        // indirect light where something traced it, a debug view's light in its place, the effects, which finish the
        // picture, and the output.
        GpuConstants header = new(gpu.Frame.Id);
        _lighting.Record(view, header, input.Shadows, input.Traced, indirect);

        // A debug view of light draws it into the picture, so the effects finish it as they do the picture.
        _surfaceView.RecordLight(view, inputs.DebugView, indirect);
        GpuTexture picture = _pixelEffects.Record(view, input.Traced, frame.Index);
        Result<GpuTexture> output = GetOutput(graph, gpu, picture);
        if (!output.Ok)
        {
            Log.Error($"The frame is not shown: {output.Error}");

            return;
        }

        _surfaceView.Record(view, inputs.DebugView, picture, output.Value);

        // The output into the window, by a linear blit: stretched where it is the view's size, copied where upscaled.
        GpuTexture target = graph.WindowTarget;
        if (!target.IsNone)
            graph.Blit(output.Value, target, RenderStage.Post);

        // One readback buffer, so one screenshot a frame: the last asked for.
        ScreenshotRequested? shot = null;
        while (_screenshots.Reader.TryRead(out ScreenshotRequested asked))
        {
            shot = asked;
        }

        if (shot is { } saved)
            SaveScreenshot(graph, output.Value, saved.Path);

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
    /// What the output is drawn into: the view's output, or one of the picture's size where an effect upscaled it.
    /// </summary>
    private static Result<GpuTexture> GetOutput(RenderGraph graph, in GpuView view, GpuTexture picture)
    {
        if (picture == view.Hdr)
            return Result.Success(view.Output);

        TextureDescription upscaled = graph.GetDescription(picture);
        return graph.Texture(
            graph.GetDescription(view.Output) with
            {
                Name = "Upscaled output",
                Width = upscaled.Width,
                Height = upscaled.Height,
            }
        );
    }

    /// <summary>
    /// Ends the start as failed: the renderer draws nothing, and holds no domain back.
    /// </summary>
    private void FailStart(string error)
    {
        Log.Error($"The renderer draws nothing: {error}");
        _loading?.TrySetResult();
        _loading = null;
        _started = Result.Failure(error);
    }

    /// <summary>
    /// The output read back, and written to <paramref name="path"/> once the GPU is done with it.
    /// </summary>
    private void SaveScreenshot(RenderGraph graph, GpuTexture output, string path)
    {
        TextureDescription size = graph.GetDescription(output);
        Result<GpuBuffer> readback = graph.Buffer(
            GpuDescriptions.GetBuffer("Output readback", (long)size.Width * size.Height * 4, BufferUsages.Readback)
        );
        if (!readback.Ok)
        {
            Log.Error($"The screenshot was not taken: {readback.Error}");

            return;
        }

        graph.ReadBack(output, readback.Value, RenderStage.Post);
        Screenshot.SaveAsync(_gpu, _assets, readback.Value, path, size.Width, size.Height).FireAndForget();
    }

    private void ApplySetting(in SetCliCommand setting)
    {
        if (setting.Key == "Deferred.window")
        {
            if (_debugWindow is not null && bool.TryParse(setting.Value, out bool isOpen))
                _debugWindow.IsOpen = isOpen;
            else
                Log.Error($"{setting.Key} takes true or false, with a debug UI to draw it, not '{setting.Value}'.");

            return;
        }

        if (setting.Key != "Deferred.debug")
            return;

        // A number parses as any value of the enum, named or not.
        if (Enum.TryParse(setting.Value, ignoreCase: true, out DebugView view) && Enum.IsDefined(view))
            _debugView = view;
        else
            Log.Error($"{setting.Key} takes {string.Join(", ", Enum.GetNames<DebugView>())}, not '{setting.Value}'.");
    }
}
