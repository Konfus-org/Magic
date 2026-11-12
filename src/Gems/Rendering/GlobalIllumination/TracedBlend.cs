using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// What a traced effect blends over frames, at the traced size: this frame's light, with its variance in alpha, and last
/// frame's, which this frame's blends with while it holds the view. It keeps two and swaps them every frame.
/// </summary>
internal static class TracedBlend
{
    /// <summary>
    /// The textures asked for under <paramref name="names"/>: the even frames write the first and the odd the second,
    /// the other being the history, which holds the view as <paramref name="hasHistory"/> says.
    /// </summary>
    public static Result<TracedBlendConstants> Prepare(
        in GIView view,
        ReadOnlySpan<string> names,
        uint frameIndex,
        bool hasHistory
    )
    {
        int written = (int)(frameIndex % 2);
        Result<GpuTexture> light = view.Graph.Texture(view.GetTraced(names[written], GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> history = view.Graph.Texture(
            view.GetTraced(names[1 - written], GpuFormat.R16G16B16A16Sfloat)
        );
        if (Result.FindFailure(light, history) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        return Result.Success(
            new TracedBlendConstants
            {
                Light = light.Value,
                LightHistory = history.Value,
                HasHistory = hasHistory ? 1u : 0u,
            }
        );
    }
}
