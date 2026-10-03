using Magic.Contexts.Rendering;
using Magic.Interfaces;
using System.Runtime.InteropServices;

namespace Magic.Extensions;

public static class RenderingExtensions
{
    extension(IRendering gpu)
    {
        /// <summary>
        /// A span of GPU structs as the bytes <see cref="IRendering.Upload(GpuBuffer, uint, ReadOnlySpan{byte})"/> takes, at a byte offset.
        /// </summary>
        public void Upload<T>(GpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            gpu.Upload(buffer, offset, MemoryMarshal.AsBytes(data));
        }
    }
}
