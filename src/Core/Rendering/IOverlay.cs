using Magic.Gpu;

namespace Magic.Rendering;

/// <summary>
/// What draws over a finished picture, as a debug UI does. A gem provides it, and whatever draws the frame calls it once
/// the picture is done, as gems can't call each other.
/// </summary>
public interface IOverlay
{
    /// <summary>
    /// Records the overlay's passes over <paramref name="target"/>, keeping what it holds. The target is drawable as a
    /// colour target; ask it for its format and size. Main thread.
    /// </summary>
    void Record(RenderGraph graph, GpuTexture target);
}
