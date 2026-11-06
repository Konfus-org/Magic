using Magic.Gpu;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The renderer's buffers and textures made in one line each, the result naming what was not made.
/// </summary>
internal static class GpuExtensions
{
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
