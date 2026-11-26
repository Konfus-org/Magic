namespace Magic.Graphics;

/// <summary>
/// How a texture's texels are laid out, with Vulkan's names: channel by channel, with the bits of each.
/// </summary>
/// <remarks>
/// Every GPU that Magic runs on has each of these formats.
/// </remarks>
public enum GpuFormat : byte
{
    Unknown,
    R8Unorm,
    R8G8B8A8Unorm,
    R8G8B8A8Srgb,
    B8G8R8A8Unorm,
    B8G8R8A8Srgb,
    A2B10G10R10Unorm,
    R16G16Unorm,
    R16G16Sfloat,
    R16G16B16A16Sfloat,
    R32Uint,
    R32Sfloat,
    D32Sfloat,
    Bc7Unorm,
    Bc7Srgb,

    /// <summary>
    /// 16-bit depth, only for the sky's direction maps: every other depth is <see cref="D32Sfloat"/>.
    /// </summary>
    D16Unorm,
}

public static class GpuFormatExtensions
{
    extension(GpuFormat format)
    {
        /// <summary>
        /// Whether the format holds depth, so that a texture of it is drawn into as the depth target.
        /// </summary>
        public bool IsDepth => format is GpuFormat.D32Sfloat or GpuFormat.D16Unorm;

        /// <summary>
        /// Whether a shader reads it as unsigned integers rather than as floats.
        /// </summary>
        public bool IsInteger => format == GpuFormat.R32Uint;
    }
}
