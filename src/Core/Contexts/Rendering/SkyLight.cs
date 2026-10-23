using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The sky's light as the renderer captured it this frame (<see cref="IRendering.CaptureSky"/>), which every shading
/// reads: the DFG table, the sky's light from everywhere as harmonics, its reflections for each roughness, and the
/// sampler they are read through. A pass's constants hold it whole as Shaders/Lighting/Ibl.hlsli's
/// <c>SkyLightIds</c>, member for member.
/// </summary>
public readonly record struct SkyLight(GpuTexture Dfg, GpuBuffer Sh, GpuTexture Specular, GpuSampler Sampler);
