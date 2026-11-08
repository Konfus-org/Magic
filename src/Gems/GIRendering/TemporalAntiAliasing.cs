using System.Collections.Immutable;
using Magic.Assets;
using Magic.Debugging;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The lit picture blended with the last frame's over the camera's jitter, so edges come out smooth and hold still.
/// The resolved picture is the next frame's history, so it keeps two and swaps them every frame, and copies the one it
/// wrote back into the HDR picture for what draws after it, which changes that in place. With no history, as at a new
/// size or after a domain, it blends with the picture itself.
/// </summary>
internal sealed class TemporalAntiAliasing(AssetManager assets)
{
    private readonly ShaderEntry _resolveShader = PassShaders.GetEntry(assets, "ResolveTemporal", "ResolveTemporal");

    /// <summary>
    /// The picture the frame drawn last resolved, which the next blends with.
    /// </summary>
    private GpuTexture _resolved;

    public ImmutableArray<ShaderEntry> Shaders => [_resolveShader];

    public void Record(in RenderView view)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> resolved = graph.Texture(GetHistory("Resolved picture", view));
        Result<GpuTexture> resolvedBefore = graph.Texture(GetHistory("Resolved picture history", view));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (Result made in (ReadOnlySpan<Result>)[resolved, resolvedBefore, sampler])
        {
            if (!made.Ok)
            {
                Log.Error($"The picture is not resolved over time: {made.Error}");

                return;
            }
        }

        // Last frame's resolved picture is this frame's history.
        (GpuTexture previous, _resolved) =
            _resolved == resolved.Value
                ? (resolved.Value, resolvedBefore.Value)
                : (resolvedBefore.Value, resolved.Value);
        if (!view.HasHistory)
            previous = view.Hdr;

        graph
            .Compute(
                "Resolve temporal",
                _resolveShader,
                new GpuConstants(view.Constants.Id, previous.Id, _resolved.Id, sampler.Value.Id)
            )
            .Reads(view.Constants, view.Hdr, view.Depth, view.Velocity, previous)
            .Writes(_resolved)
            .DispatchOver(view.Width, view.Height);
        graph.Blit(_resolved, view.Hdr);
    }

    private static TextureDescription GetHistory(string name, in RenderView view) =>
        GpuDescriptions.GetTexture(
            name,
            view.Width,
            view.Height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
        );
}
