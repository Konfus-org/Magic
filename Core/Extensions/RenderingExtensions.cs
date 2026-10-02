using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Utils;
using System.Runtime.InteropServices;

namespace Magic.Extensions;

public static class RenderingExtensions
{
    extension(IRendering gpu)
    {
        /// <summary>A span of GPU structs as the bytes <see cref="IRendering.Upload(GpuBuffer, uint, ReadOnlySpan{byte})"/> takes, at a byte offset.</summary>
        public void Upload<T>(GpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            gpu.Upload(buffer, offset, MemoryMarshal.AsBytes(data));
        }

        /// <summary>
        /// What <paramref name="window"/> last showed, before anything drawn over it, as a PNG at <paramref name="path"/>.
        /// Waits for the GPU. Failed, with why, when nothing has been shown in it yet or the file could not be written.
        /// </summary>
        public Result Screenshot(IWindow window, IFileSystem files, string path)
        {
            Result<CapturedFrame> captured = gpu.Read(GpuTexture.Window(window.Handle));
            if (captured.Failed)
                return Result.Failure(captured.Message);

            return files.WriteBinary(path, Png.Encode(captured.Payload.Width, captured.Payload.Height, captured.Payload.Pixels));
        }
    }
}
