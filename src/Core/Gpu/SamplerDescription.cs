using Magic.Files.Types;

namespace Magic.Gpu;

/// <summary>
/// What a sampler makes of the texels under a sample: their blend, or the least or greatest of them, which is what a
/// depth pyramid is built with.
/// </summary>
public enum SamplerReduction : byte
{
    Average,
    Min,
    Max,
}

/// <summary>
/// How to make a sampler.
/// </summary>
public readonly record struct SamplerDescription
{
    public SamplerDescription() { }

    public TextureFilter Filter { get; init; } = TextureFilter.Linear;

    public TextureWrap Wrap { get; init; } = TextureWrap.Repeat;

    /// <summary>
    /// How a sample picks between mip levels: a blend of the two nearest, or the nearest alone.
    /// </summary>
    public TextureFilter MipFilter { get; init; }

    /// <summary>
    /// Samples taken along a surface seen at a slant, 1 to 16; 1 is none.
    /// </summary>
    public int Anisotropy { get; init; } = 1;

    public SamplerReduction Reduction { get; init; }
}
