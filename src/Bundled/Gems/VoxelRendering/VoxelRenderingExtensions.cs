using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.VoxelRendering;

/// <summary>
/// Making a buffer that a count kept in it starts from none in.
/// </summary>
internal static class VoxelRenderingExtensions
{
    extension(IGpuDriver gpu)
    {
        /// <summary>
        /// A buffer of <paramref name="bytes"/>, at least a word, filled with zeros.
        /// </summary>
        internal GpuBuffer CreateZeroedBuffer(GpuBufferUsage usage, uint bytes)
        {
            uint size = Math.Max(bytes, 4);
            GpuBuffer buffer = gpu.CreateBuffer(usage, size);
            gpu.Upload(buffer, 0, new byte[size]);

            return buffer;
        }
    }
}
