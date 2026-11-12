using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.TaaUpscaling;

/// <summary>
/// Takes the finished picture to the size it is shown at with temporal anti-aliasing: each frame it makes the
/// <see cref="Upscaling"/>, so the camera jitters, and the renderer has it record one pass once the view's output is
/// drawn. The pass builds each shown pixel from the output's jittered texels around it and blends it with last
/// frame's resolved picture where its surface was, so it keeps two resolved pictures and swaps them every frame,
/// with no history on the first frame at a size or of a world; the newer is what the window shows. It makes nothing
/// while its shader builds or after it failed.
/// </summary>
public sealed class Renderer : IRenderInto<Upscaling>, IHookIntoDomainLoading
{
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
    /// The picture the frame drawn last resolved, the next one's history, and its size.
    /// </summary>
    private (GpuTexture Picture, int Width, int Height) _resolved;

    public Renderer(AssetManager assets)
    {
        _shader = PassShaders.GetEntry(assets, "TaaUpscaling", "UpscalePicture");
        _upscaling = new Upscaling(Record);
    }

    /// <summary>
    /// The frame's upscaling; none while the shader builds or after it failed.
    /// </summary>
    public Upscaling? Render(in Frame frame, RenderGraph graph)
    {
        GpuState state = graph.GetState(_shader);
        if (state == GpuState.Failed)
            graph.Fail(RenderFailure.Shader, "Picture is not upscaled", "the upscaling's shader was not built.");

        if (state != GpuState.Loading)
            _built.TrySetResult();

        return state == GpuState.Ready ? _upscaling : null;
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    private static TextureDescription GetResolved(string name, int width, int height) =>
        new(
            name,
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
        );

    /// <summary>
    /// The pass that resolves the view's output at the size it is shown at, and the picture it resolves; none when
    /// its pictures were not made.
    /// </summary>
    private GpuTexture Record(RenderGraph graph, GpuView view)
    {
        (int width, int height) = (view.ShownWidth, view.ShownHeight);
        Result<GpuTexture> first = graph.Texture(GetResolved("Resolved picture", width, height));
        Result<GpuTexture> second = graph.Texture(GetResolved("Other resolved picture", width, height));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (Result.FindFailure(first, second, sampler) is { Ok: false } failed)
        {
            graph.Fail(RenderFailure.Resource, "Picture is not upscaled", failed.Error);

            return GpuTexture.None;
        }

        // Last frame's resolved picture is this frame's history, where last frame drew this view at this size; the
        // shader reads none where the history it is handed is the picture it resolves.
        (GpuTexture history, GpuTexture resolved) =
            _resolved.Picture == first.Value ? (first.Value, second.Value) : (second.Value, first.Value);
        bool hasHistory = view.HasHistory && _resolved == (history, width, height);
        _resolved = (resolved, width, height);
        ReadOnlySpan<GpuResource> past = hasHistory ? [history] : [];
        graph
            .Compute(
                "Upscale picture",
                _shader,
                new GpuConstants(view.Frame.Id, hasHistory ? history.Id : resolved.Id, resolved.Id, sampler.Value.Id),
                RenderStages.Post
            )
            .Reads(view.Frame, view.Output, view.Depth, view.Velocity)
            .Reads(past)
            .Writes(resolved)
            .DispatchOver(width, height);

        return resolved;
    }
}
