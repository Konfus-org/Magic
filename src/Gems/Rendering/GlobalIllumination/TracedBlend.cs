using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// What a traced effect blends over frames, at the traced size: this frame's light, with its variance in alpha, and the
/// view depth of each of its pixels, and last frame's of both, which this frame's blends with while the view holds a
/// history. It keeps two of each and swaps them every frame.
/// </summary>
internal static class TracedBlend
{
    /// <summary>
    /// The textures asked for under <paramref name="names"/>, the first light and the second, then the first depth and
    /// the second: the even frames write the first of each and the odd the second, the other being the history, which
    /// holds the view as <paramref name="view"/> says.
    /// </summary>
    public static Result<TracedBlendConstants> Prepare(in GIView view, ReadOnlySpan<string> names, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        int written = (int)(frameIndex % 2);
        Result<GpuTexture> light = graph.Texture(view.GetTraced(names[written], GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> depth = graph.Texture(view.GetTraced(names[2 + written], GpuFormat.R32Sfloat));
        Result<GpuTexture> lightHistory = graph.Texture(
            view.GetTraced(names[1 - written], GpuFormat.R16G16B16A16Sfloat)
        );
        Result<GpuTexture> depthHistory = graph.Texture(view.GetTraced(names[3 - written], GpuFormat.R32Sfloat));
        if (Result.FindFailure(light, depth, lightHistory, depthHistory) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        return Result.Success(
            new TracedBlendConstants
            {
                Light = light.Value,
                Depth = depth.Value,
                LightHistory = lightHistory.Value,
                DepthHistory = depthHistory.Value,
                HasHistory = view.HasHistory ? 1u : 0u,
            }
        );
    }
}
