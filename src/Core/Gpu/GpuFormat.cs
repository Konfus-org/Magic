namespace Magic.Gpu;

/// <summary>
/// How a texture's texels are laid out, named as Vulkan names them, channel by channel with the bits of each. Only the
/// formats the renderer needs, every one of which every GPU Magic runs on has.
/// </summary>
public enum GpuFormat : byte
{
    Unknown,
    R8Unorm,
    R8Snorm,
    R8G8Unorm,
    R8G8B8A8Unorm,
    R8G8B8A8Srgb,
    B8G8R8A8Unorm,
    B8G8R8A8Srgb,
    A2B10G10R10Unorm,
    B10G11R11Ufloat,
    R16Uint,
    R16G16Unorm,
    R16G16Sfloat,
    R16G16B16A16Sfloat,
    R32Uint,
    R32Sfloat,
    R32G32Uint,
    R32G32Sfloat,
    R32G32B32A32Sfloat,
    D32Sfloat,
    Bc4Unorm,
    Bc5Unorm,
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
        public bool IsInteger => format is GpuFormat.R16Uint or GpuFormat.R32Uint or GpuFormat.R32G32Uint;

        /// <summary>
        /// Whether the format is block compressed, a 4 by 4 block of texels in each block.
        /// </summary>
        public bool IsCompressed => format >= GpuFormat.Bc4Unorm;

        /// <summary>
        /// Bytes in one texel, or in one 4 by 4 block for a compressed format.
        /// </summary>
        public int BlockBytes =>
            format switch
            {
                GpuFormat.Unknown => 0,
                GpuFormat.R8Unorm or GpuFormat.R8Snorm => 1,
                GpuFormat.R8G8Unorm or GpuFormat.R16Uint => 2,
                GpuFormat.R8G8B8A8Unorm
                or GpuFormat.R8G8B8A8Srgb
                or GpuFormat.B8G8R8A8Unorm
                or GpuFormat.B8G8R8A8Srgb
                or GpuFormat.A2B10G10R10Unorm
                or GpuFormat.B10G11R11Ufloat
                or GpuFormat.R16G16Unorm
                or GpuFormat.R16G16Sfloat
                or GpuFormat.R32Uint
                or GpuFormat.R32Sfloat
                or GpuFormat.D32Sfloat => 4,
                GpuFormat.R16G16B16A16Sfloat or GpuFormat.R32G32Uint or GpuFormat.R32G32Sfloat or GpuFormat.Bc4Unorm =>
                    8,
                GpuFormat.R32G32B32A32Sfloat or GpuFormat.Bc5Unorm or GpuFormat.Bc7Unorm or GpuFormat.Bc7Srgb => 16,
                _ => 0,
            };

        /// <summary>
        /// Bytes one level of <paramref name="width"/> by <paramref name="height"/> texels takes, tightly packed.
        /// </summary>
        public long GetLevelBytes(int width, int height)
        {
            if (!format.IsCompressed)
                return (long)width * height * format.BlockBytes;

            long blocksAcross = (width + 3) / 4;
            long blocksDown = (height + 3) / 4;

            return blocksAcross * blocksDown * format.BlockBytes;
        }
    }
}
