namespace Magic.Gpu;

/// <summary>
/// How a texture's texels are laid out, named as Vulkan names them, channel by channel with the bits of each. Only the
/// formats the renderer needs, every one of which every GPU Magic runs on has.
/// </summary>
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
}

public static class GpuFormatExtensions
{
    extension(GpuFormat format)
    {
        /// <summary>
        /// Whether the format holds depth, which a texture of it is drawn into as the depth target.
        /// </summary>
        public bool IsDepth => format == GpuFormat.D32Sfloat;

        /// <summary>
        /// Whether a shader reads it as unsigned integers rather than as floats.
        /// </summary>
        public bool IsInteger => format == GpuFormat.R32Uint;
    }
}
