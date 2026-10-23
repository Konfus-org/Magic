using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

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
public enum GpuWrap : byte
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

/// <summary>
/// A sampler for <see cref="IGpuDriver.CreateSampler"/> to make. <paramref name="Anisotropy"/> above 1 filters
/// anisotropically with up to that many taps. With <paramref name="Compare"/> it is a comparison sampler
/// (<c>SamplerComparisonState</c>): a tap is 1 where <c>reference op stored</c> holds, filtered over the taps.
/// </summary>
public readonly record struct GpuSamplerDescription(
    GpuFilter Filter,
    GpuWrap Wrap,
    float Anisotropy = 1,
    GpuCompare? Compare = null);
