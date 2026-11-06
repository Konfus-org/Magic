using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The textures a frame draws through, all the output's size, made again when that changes: the visibility buffer
/// and its depth, the depth pyramid the next frame culls against, the GBuffer, and the output with the buffer its
/// pixels are read back through for a screenshot.
/// </summary>
internal sealed class FrameTargets(IGraphicsApi gpu, RenderGraph graph) : IDisposable
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

    /// <summary>
    /// Where each pixel's surface was on last frame's screen, as the move from its place now in texture
    /// coordinates, and its view depth then.
    /// </summary>
    public GpuTexture Velocity { get; private set; }

    /// <summary>
    /// The lit picture, in the frame's light units before the exposure.
    /// </summary>
    public GpuTexture Hdr { get; private set; }

    public GpuTexture Output { get; private set; }

    public GpuBuffer Readback { get; private set; }

    public void Dispose() => Destroy();

    /// <summary>
    /// Makes the targets at a size, the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height)
    {
        Destroy();
        Width = width;
        Height = height;
        HzbLevels = TextureDescription.GetFullLevels(width, height);
        Result<GpuTexture> visibility = gpu.CreateTexture(
            "Visibility",
            width,
            height,
            GpuFormat.R32Uint,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> depth = gpu.CreateTexture(
            "Depth",
            width,
            height,
            GpuFormat.D32Sfloat,
            TextureUsages.DepthTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> hzb = gpu.CreateTexture(
            "Depth pyramid",
            width,
            height,
            GpuFormat.R32Sfloat,
            levels: HzbLevels
        );
        Result<GpuTexture> normal = gpu.CreateTexture("GBuffer normal", width, height, GpuFormat.R16G16Unorm);
        Result<GpuTexture> baseColor = gpu.CreateTexture("GBuffer base", width, height, GpuFormat.R8G8B8A8Unorm);
        Result<GpuTexture> surface = gpu.CreateTexture("GBuffer surface", width, height, GpuFormat.R8G8B8A8Unorm);
        Result<GpuTexture> velocity = gpu.CreateTexture("Velocity", width, height, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> hdr = gpu.CreateTexture("HDR", width, height, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> output = gpu.CreateTexture(
            "Output",
            width,
            height,
            GpuFormat.R8G8B8A8Unorm,
            TextureUsages.Storage | TextureUsages.ColorTarget | TextureUsages.CopySource
        );
        Result<GpuBuffer> readback = gpu.CreateBuffer(
            "Output readback",
            (long)width * height * 4,
            BufferUsages.Readback
        );
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [visibility, depth, hzb, normal, baseColor, surface, velocity, hdr, output, readback]
        )
        {
            if (!made.Ok)
                return made;
        }

        Visibility = visibility.Value;
        Depth = depth.Value;
        Hzb = hzb.Value;
        GBufferNormal = normal.Value;
        GBufferBase = baseColor.Value;
        GBufferSurface = surface.Value;
        Velocity = velocity.Value;
        Hdr = hdr.Value;
        Output = output.Value;
        Readback = readback.Value;

        return Result.Success();
    }

    private void Destroy()
    {
        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [Visibility, Depth, Hzb, GBufferNormal, GBufferBase, GBufferSurface, Velocity, Hdr, Output]
        )
        {
            graph.Forget(texture);
            gpu.Destroy(texture);
        }

        graph.Forget(Readback);
        gpu.Destroy(Readback);

        Visibility = GpuTexture.None;
        Depth = GpuTexture.None;
        Hzb = GpuTexture.None;
        GBufferNormal = GpuTexture.None;
        GBufferBase = GpuTexture.None;
        GBufferSurface = GpuTexture.None;
        Velocity = GpuTexture.None;
        Hdr = GpuTexture.None;
        Output = GpuTexture.None;
        Readback = GpuBuffer.None;
        Width = 0;
        Height = 0;
    }
}
