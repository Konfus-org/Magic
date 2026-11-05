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

    public GpuTexture Hdr { get; private set; }

    /// <summary>
    /// The HDR picture with the panes composed over it: what the post resolves.
    /// </summary>
    public GpuTexture Picture { get; private set; }

    /// <summary>
    /// The output's size, which the picture is resolved into: the window's, whatever the picture is drawn at.
    /// </summary>
    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    /// <summary>
    /// The translucent surfaces' power moments of depth, their absorbance by colour and in all, the light they add
    /// with its weight, and how far they bend the picture behind them.
    /// </summary>
    public GpuTexture Moments { get; private set; }

    public GpuTexture OpticalDepth { get; private set; }

    public GpuTexture Accumulation { get; private set; }

    public GpuTexture Offsets { get; private set; }

    public GpuTexture Output { get; private set; }

    public GpuBuffer Readback { get; private set; }

    public void Dispose() => Destroy(null);

    /// <summary>
    /// Makes the targets at a size, the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height, int outputWidth, int outputHeight)
    {
        Destroy(graph);
        Width = width;
        Height = height;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
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
        Result<GpuTexture> velocity = Make(
            "Velocity",
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled
        );
        Result<GpuTexture> hdr = Make(
            "HDR",
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled
        );
        Result<GpuTexture> picture = Make(
            "Picture",
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled
        );
        Result<GpuTexture> moments = Make(
            "Translucent moments",
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> opticalDepth = Make(
            "Translucent absorbance",
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> accumulation = Make(
            "Translucent light",
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> offsets = Make(
            "Translucent offsets",
            GpuFormat.R16G16Sfloat,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> output = gpu.CreateTexture(
            new TextureDescription
            {
                Width = outputWidth,
                Height = outputHeight,
                Format = GpuFormat.R8G8B8A8Unorm,
                Usage = TextureUsages.Storage | TextureUsages.ColorTarget | TextureUsages.CopySource,
                Name = "Output",
            }
        );
        Result<GpuBuffer> readback = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)outputWidth * outputHeight * 4,
                Usage = BufferUsages.Readback,
                Name = "Output readback",
            }
        );
        foreach (
            Result<GpuTexture> made in (ReadOnlySpan<Result<GpuTexture>>)
                [
                    visibility,
                    depth,
                    hzb,
                    normal,
                    baseColor,
                    surface,
                    velocity,
                    hdr,
                    picture,
                    moments,
                    opticalDepth,
                    accumulation,
                    offsets,
                    output,
                ]
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
        Velocity = velocity.Value;
        Hdr = hdr.Value;
        Picture = picture.Value;
        Moments = moments.Value;
        OpticalDepth = opticalDepth.Value;
        Accumulation = accumulation.Value;
        Offsets = offsets.Value;
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

    private void Destroy(RenderGraph? forgetIn)
    {
        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [
                    Visibility,
                    Depth,
                    Hzb,
                    GBufferNormal,
                    GBufferBase,
                    GBufferSurface,
                    Velocity,
                    Hdr,
                    Picture,
                    Moments,
                    OpticalDepth,
                    Accumulation,
                    Offsets,
                    Output,
                ]
        )
        {
            if (texture.IsNone)
                continue;

            forgetIn?.Forget(texture);
            gpu.Destroy(texture);
        }

        if (!Readback.IsNone)
        {
            forgetIn?.Forget(Readback);
            gpu.Destroy(Readback);
        }

        Visibility = GpuTexture.None;
        Depth = GpuTexture.None;
        Hzb = GpuTexture.None;
        GBufferNormal = GpuTexture.None;
        GBufferBase = GpuTexture.None;
        GBufferSurface = GpuTexture.None;
        Velocity = GpuTexture.None;
        Hdr = GpuTexture.None;
        Picture = GpuTexture.None;
        Moments = GpuTexture.None;
        OpticalDepth = GpuTexture.None;
        Accumulation = GpuTexture.None;
        Offsets = GpuTexture.None;
        Output = GpuTexture.None;
        Readback = GpuBuffer.None;
        Width = 0;
        Height = 0;
    }
}
