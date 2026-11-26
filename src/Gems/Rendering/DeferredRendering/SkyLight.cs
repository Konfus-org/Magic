using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The sky's light for the view's ambient light, made from the sky as it is this frame: its irradiance and its
/// prefiltered radiance.
/// </summary>
/// <remarks>
/// <see cref="Irradiance"/> holds 10 rows of 4 floats: 9 coefficients of RGB spherical harmonics up to the second band,
/// then the radiance of the ground below the horizon. <see cref="Map"/> is an octahedral map of the whole sphere in
/// <c>R16G16B16A16Sfloat</c>, whose level l is prefiltered by GGX for a perceptual roughness of l over its last level.
/// The shading's step writes their ids into the frame's buffer as <see cref="GpuAmbient"/>.
/// </remarks>
public readonly record struct SkyLight(GpuBuffer Irradiance, GpuTexture Map, int MapLevels, GpuSampler Sampler);

/// <summary>
/// How much of the sky each pixel of the view sees, from 0 to 1: diffuse in red and specular in green, at half the
/// picture's size in <c>R16G16Unorm</c>.
/// </summary>
/// <remarks>
/// The shading's step writes its id into the frame's buffer as <see cref="GpuAmbient"/>, and multiplies only the sky's
/// light by it, upsampled to each pixel by depth and normal.
/// </remarks>
public readonly record struct SkyVisibility(GpuTexture Visibility);
