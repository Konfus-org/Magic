using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The sky's three tables for a view: the light the air lets through by height and direction, the light it scatters
/// again by height and sun, and the sky as seen from the camera's height.
/// </summary>
/// <remarks>
/// Their ids are in the frame's buffer as <see cref="GpuSkyTables"/>. There are none where no entity has a sky.
/// </remarks>
public readonly record struct SkyTables(GpuTexture Transmittance, GpuTexture MultiScattering, GpuTexture View);
