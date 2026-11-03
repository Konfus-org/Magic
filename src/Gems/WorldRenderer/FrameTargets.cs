using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The textures a frame draws through, all the output's size, made again when that changes: the visibility buffer
/// and its depth, the depth pyramid the next frame culls against, the GBuffer, and the output with the buffer its
/// pixels are read back through for a screenshot.
/// </summary>
internal sealed class FrameTargets(IGraphicsApi gpu) : IDisposable
{
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int HzbLevels { get; private set; }

    public GpuTexture Visibility { get; private set; }

    public GpuTexture Depth { get; private set; }

    public GpuTexture Hzb { get; private set; }

    public GpuTexture GBufferNormal { get; private set; }

    public GpuTexture GBufferBase { get; private set; }

    public GpuTexture GBufferSurface { get; private set; }

    public GpuTexture Output { get; private set; }

    public GpuBuffer Readback { get; private set; }

    public void Dispose() => Destroy(null);

    /// <summary>
    /// Makes the targets at a size, the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height, RenderGraph graph)
    {
        Destroy(graph);
        Width = width;
        Height = height;
        HzbLevels = TextureDescription.GetFullLevels(width, height);
        Result<GpuTexture> visibility = Make(
            "Visibility",
            GpuFormat.R32Uint,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> depth = Make(
            "Depth",
            GpuFormat.D32Sfloat,
            TextureUsages.DepthTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> hzb = Make(
            "Depth pyramid",
            GpuFormat.R32Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled,
            HzbLevels
        );
        Result<GpuTexture> normal = Make(
            "GBuffer normal",
            GpuFormat.R16G16Unorm,
            TextureUsages.Storage | TextureUsages.Sampled
        );
        Result<GpuTexture> baseColor = Make(
            "GBuffer base",
            GpuFormat.R8G8B8A8Unorm,
            TextureUsages.Storage | TextureUsages.Sampled
        );
        Result<GpuTexture> surface = Make(
            "GBuffer surface",
            GpuFormat.R8G8B8A8Unorm,
            TextureUsages.Storage | TextureUsages.Sampled
        );
        Result<GpuTexture> output = Make(
            "Output",
            GpuFormat.R8G8B8A8Unorm,
            TextureUsages.Storage | TextureUsages.CopySource
        );
        Result<GpuBuffer> readback = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)width * height * 4,
                Usage = BufferUsages.Readback,
                Name = "Output readback",
            }
        );
        foreach (
            Result<GpuTexture> made in (ReadOnlySpan<Result<GpuTexture>>)
                [visibility, depth, hzb, normal, baseColor, surface, output]
        )
        {
            if (!made.Ok)
                return Result.Failure($"a frame target was not made: {made.Error}");
        }

        if (!readback.Ok)
            return Result.Failure($"the output's readback buffer was not made: {readback.Error}");

        Visibility = visibility.Value;
        Depth = depth.Value;
        Hzb = hzb.Value;
        GBufferNormal = normal.Value;
        GBufferBase = baseColor.Value;
        GBufferSurface = surface.Value;
        Output = output.Value;
        Readback = readback.Value;

        return Result.Success();
    }

    private Result<GpuTexture> Make(string name, GpuFormat format, TextureUsages usage, int levels = 1) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = Width,
                Height = Height,
                Format = format,
                Usage = usage,
                Levels = levels,
                Name = name,
            }
        );

    private void Destroy(RenderGraph? graph)
    {
        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [Visibility, Depth, Hzb, GBufferNormal, GBufferBase, GBufferSurface, Output]
        )
        {
            if (texture.IsNone)
                continue;

            graph?.Forget(texture);
            gpu.Destroy(texture);
        }

        if (!Readback.IsNone)
        {
            graph?.Forget(Readback);
            gpu.Destroy(Readback);
        }

        Visibility = GpuTexture.None;
        Depth = GpuTexture.None;
        Hzb = GpuTexture.None;
        GBufferNormal = GpuTexture.None;
        GBufferBase = GpuTexture.None;
        GBufferSurface = GpuTexture.None;
        Output = GpuTexture.None;
        Readback = GpuBuffer.None;
        Width = 0;
        Height = 0;
    }
}
