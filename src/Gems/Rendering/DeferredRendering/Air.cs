using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The air between the camera and what it sees this frame: a volume of scattered light over the view, and the clouds.
/// </summary>
/// <remarks>
/// The volume holds the light the air scatters towards the camera and how much of what is behind gets through. The
/// clouds are a picture over the sky, and none where the sky has no clouds. Their ids are in the frame's buffer as
/// <see cref="GpuAir"/>.
/// </remarks>
public readonly record struct Air(GpuTexture Volume, GpuTexture? Clouds);
