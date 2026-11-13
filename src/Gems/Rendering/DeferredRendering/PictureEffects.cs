using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The effects that run over the view's lit picture before the output.
/// </summary>
/// <remarks>
/// A gem that runs post-processing makes it each frame, before the view. The renderer calls <paramref name="Record"/>
/// with the view and the frame's index once the picture is lit, and the effects leave their picture in the view's
/// <see cref="GpuView.Hdr"/>. While <paramref name="IsJittered"/>, the camera jitters.
/// </remarks>
public readonly record struct PictureEffects(bool IsJittered, RecordsEffects Record);

/// <summary>
/// Records the effects of the frame <paramref name="frameIndex"/> over the lit picture of <paramref name="view"/>.
/// </summary>
public delegate void RecordsEffects(RenderGraph graph, GpuView view, uint frameIndex);
