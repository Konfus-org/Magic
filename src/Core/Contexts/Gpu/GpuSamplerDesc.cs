using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A sampler for <see cref="IGpu.CreateSampler"/> to make. <paramref name="Anisotropy"/> above 1 filters anisotropically
/// with up to that many taps. With <paramref name="Compare"/> it is a comparison sampler
/// (<c>SamplerComparisonState</c>): a tap is 1 where <c>reference op stored</c> holds, filtered over the taps.
/// </summary>
public readonly record struct GpuSamplerDesc(
    GpuFilter Filter,
    GpuAddress Address,
    float Anisotropy = 1,
    GpuCompare? Compare = null);

/// <summary>
/// How a sampler reads between texels and between mip levels.
/// </summary>
public enum GpuFilter : byte
{
    Linear,
    Nearest,
}

/// <summary>
/// What a sampler reads past a texture's edge.
/// </summary>
public enum GpuAddress : byte
{
    /// <summary>
    /// The edge texel.
    /// </summary>
    Clamp,

    /// <summary>
    /// The texture again.
    /// </summary>
    Repeat,

    /// <summary>
    /// The texture again, mirrored every other time.
    /// </summary>
    Mirror,
}
