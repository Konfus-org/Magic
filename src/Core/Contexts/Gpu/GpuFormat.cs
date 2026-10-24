namespace Magic.Contexts.Gpu;

/// <summary>
/// The texture formats the engine renders with. Depth is always <see cref="D32Float"/>, reverse-Z
/// (Shaders/README.md). The BC7 formats are only sampled: a texture of them is uploaded a 4 x 4 block of
/// texels in each 16 bytes, a level smaller than a block taking one.
/// </summary>
public enum GpuFormat : byte
{
    Invalid,
    R8Unorm,
    Rg8Unorm,
    Rgba8Unorm,
    Rgba8Srgb,
    Bgra8Unorm,
    Bgra8Srgb,
    Rgb10A2Unorm,
    R16Float,
    Rg16Float,
    Rgba16Float,
    R32Float,
    Rg32Float,
    Rgba32Float,
    R11G11B10Float,
    R32Uint,
    D32Float,
    Bc7Unorm,
    Bc7Srgb,
}
