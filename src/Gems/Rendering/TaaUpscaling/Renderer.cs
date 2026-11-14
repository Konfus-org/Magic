using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.TaaUpscaling;

/// <summary>
/// Takes the finished picture to the size it is shown at with temporal anti-aliasing.
/// </summary>
/// <remarks>
/// Each frame it makes the <see cref="Upscaling"/>, so the camera jitters, and the renderer has it record one pass once
/// the view's output is drawn. The pass builds each shown pixel from the output's jittered texels around it and blends
/// it with last frame's resolved picture where its surface was. It keeps two resolved pictures and swaps them every
/// frame, with no history on the first frame at a size or of a world; the newer is what the window shows. It makes
/// nothing while its shader builds or after it failed.
/// </remarks>
public sealed class Renderer : IRenderInto<Upscaling>, IHookIntoDomainLoading
{
    /// <summary>
    /// The frames a view's history blends before its picture is settled. Until then each frame takes at least as much
    /// as each before it since the history started afresh, so after 16 each of the world's frames weighs at most a
    /// sixteenth.
    /// </summary>
    private const int SettleFrames = 16; // PROTO

    /// <summary>
    /// Done once the shader is built or failed, which a domain loading waits for, so a shader that fails is told and
    /// logged before the world shows.
    /// </summary>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ShaderEntry _shader;

    /// <summary>
    /// The one upscaling every frame makes, so a frame makes no delegate.
    /// </summary>
    private readonly Upscaling _upscaling;

    /// <summary>
    /// The picture the frame drawn last resolved in each view, the next one's history, its size, and the frames its
    /// history blended.
    /// </summary>
    private readonly List<(GpuTexture Picture, int Width, int Height, int Blended)> _resolved = [];

    /// <summary>
    /// The wait of the world that arrived for the resolved picture to settle, which the frame that blends enough frames
    /// or upscales nothing completes. Render thread.
    /// </summary>
    private TaskCompletionSource? _settling;

    private readonly Threads _threads;

    internal Renderer(AssetManager assets, Threads threads)
    {
        _threads = threads;
        _shader = PassShaders.GetEntry(assets, "TaaUpscaling", "UpscalePicture");
        _upscaling = new Upscaling(Record);
    }

    /// <summary>
    /// Makes the frame's upscaling; none while the shader builds or after it failed.
    /// </summary>
    public Upscaling? Render(in Frame frame, RenderGraph graph)
    {
        GpuState state = graph.GetState(_shader);
        if (state == GpuState.Failed)
            graph.Fail(RenderFailure.Shader, "Picture is not upscaled", "the upscaling's shader was not built.");

        if (state != GpuState.Loading)
            _built.TrySetResult();

        // A view maker that is out leaves the upscaling no view to resolve for good: the failure is already told.
        if (state != GpuState.Ready || graph.IsMakerOut<GpuView>())
            _settling?.TrySetResult();

        return state == GpuState.Ready ? _upscaling : null;
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    /// <summary>
    /// Returns once the resolved picture of a view blended enough frames of the world that arrived, or at once while it
    /// upscales nothing.
    /// </summary>
    public async Task OnDomainArrivedAsync(CancellationToken cancel)
    {
        await _threads.Enter(EngineThread.Render);
        TaskCompletionSource settling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _settling = settling;
        await settling.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    private static TextureDescription GetResolved(string name, int width, int height) =>
        new(
            name,
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
        );

    /// <summary>
    /// Records the pass that resolves the view's output at the size it is shown at.
    /// </summary>
    /// <returns>The picture it resolves; none when its pictures were not made.</returns>
    private GpuTexture Record(RenderGraph graph, GpuView view)
    {
        (int width, int height) = (view.ShownWidth, view.ShownHeight);
        Result<GpuTexture> first = graph.Texture(GetResolved("Resolved picture", width, height));
        Result<GpuTexture> second = graph.Texture(GetResolved("Other resolved picture", width, height));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        Result<GpuBuffer> constants = graph.Buffer(new("Upscale constants", Unsafe.SizeOf<UpscaleConstants>()));
        if (Result.FindFailure(first, second, sampler, constants) is { Ok: false } failed)
        {
            _settling?.TrySetResult();
            graph.Fail(RenderFailure.Resource, "Picture is not upscaled", failed.Error);

            return GpuTexture.None;
        }

        // Last frame's resolved picture is this frame's history, where last frame drew this view at this size; the
        // shader reads none where the history it is handed is the picture it resolves.
        while (_resolved.Count <= graph.View)
        {
            _resolved.Add(default);
        }

        (GpuTexture lastPicture, int lastWidth, int lastHeight, int blended) = _resolved[graph.View];
        (GpuTexture history, GpuTexture resolved) =
            lastPicture == first.Value ? (first.Value, second.Value) : (second.Value, first.Value);
        bool hasHistory = view.HasHistory && (lastPicture, lastWidth, lastHeight) == (history, width, height);
        blended = hasHistory ? blended + 1 : 0;
        _resolved[graph.View] = (resolved, width, height, blended);
        if (blended >= SettleFrames)
            _settling?.TrySetResult();

        UpscaleConstants upscale = new()
        {
            Sampler = sampler.Value.Id,
            LeastBlend = blended < SettleFrames ? 1f / (blended + 1) : 0,
        };
        if (!graph.Upload(constants.Value, [upscale]))
            return GpuTexture.None;

        ReadOnlySpan<GpuResource> past = hasHistory ? [history] : [];
        graph
            .Compute(
                "Upscale picture",
                _shader,
                new GpuConstants(view.Frame.Id, hasHistory ? history.Id : resolved.Id, resolved.Id, constants.Value.Id),
                RenderStages.Post
            )
            .Reads(view.Frame, constants.Value, view.Output, view.Depth, view.Velocity)
            .Reads(past)
            .Writes(resolved)
            .DispatchOver(width, height);

        return resolved;
    }
}

/// <summary>
/// The upscaling pass's sampler and how much of now it takes at least, the twin of <c>TaaUpscaling.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct UpscaleConstants
{
    /// <summary>
    /// The linear clamp sampler the history is read through.
    /// </summary>
    public uint Sampler;

    /// <summary>
    /// How much of now each pixel takes at least where the history is on the screen: more while the history settles.
    /// </summary>
    public float LeastBlend;
}
