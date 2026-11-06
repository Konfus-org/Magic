using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The renderer's buffers and textures made in one line each, the result naming what was not made, and a buffer filled
/// in one line.
/// </summary>
internal static class GpuExtensions
{
    extension(RenderGraph graph)
    {
        /// <summary>
        /// Copies <paramref name="rows"/> into <paramref name="to"/> from <paramref name="offset"/> bytes on, staged
        /// through an upload, in a transfer pass the caller opened.
        /// </summary>
        public void Upload<T>(IGraphicsApi gpu, GpuBuffer to, ReadOnlySpan<T> rows, ulong offset = 0)
            where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(rows);
            GpuUpload upload = gpu.BeginUpload(bytes.Length);
            bytes.CopyTo(upload.Bytes);
            graph.Use(to, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, to, offset, (ulong)bytes.Length);
        }

        public void Upload<T>(IGraphicsApi gpu, GpuBuffer to, in T value)
            where T : unmanaged => graph.Upload(gpu, to, new ReadOnlySpan<T>(in value));
    }

    extension(IGraphicsApi gpu)
    {
        public Result<GpuBuffer> CreateBuffer(string name, long bytes, BufferUsages usage = BufferUsages.Storage)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = bytes,
                    Usage = usage,
                    Name = name,
                }
            );

            return buffer.Ok ? buffer : Result.Failure($"the {name} was not made: {buffer.Error}");
        }

        public Result<GpuTexture> CreateTexture(
            string name,
            int width,
            int height,
            GpuFormat format,
            TextureUsages usage = TextureUsages.Storage | TextureUsages.Sampled,
            int levels = 1
        )
        {
            Result<GpuTexture> texture = gpu.CreateTexture(
                new TextureDescription
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    Usage = usage,
                    Levels = levels,
                    Name = name,
                }
            );

            return texture.Ok ? texture : Result.Failure($"the {name} was not made: {texture.Error}");
        }
    }
}
