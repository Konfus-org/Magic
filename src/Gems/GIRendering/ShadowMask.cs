using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// On a GPU that traces rays, how much of each light reaches each pixel: a pass that traces a ray a pixel towards the
/// sun and each of the first lights its tile lists, into a buffer of 16 bytes a pixel, the layout
/// <c>Scene/Shadows.slang</c> reads, and the sun's tiles beside it, which the denoiser makes the mask shading reads of.
/// </summary>
internal sealed class ShadowMask(
    IGraphicsApi gpu,
    RenderGraph graph,
    GpuPipelines pipelines,
    GeometryPools pools,
    Visibility visibility,
    LightTiles lights
) : IDisposable
{
    private const int BytesPerPixel = 16;
    private const int SunTileWidth = 8;
    private const int SunTileHeight = 4;

    private readonly GpuPass _tracePass = pipelines.Compute(
        "Trace shadows",
        "TraceShadows",
        "TraceShadows",
        isTiled: true
    );

    private readonly ShadowDenoiser _denoiser = new(gpu, graph, pipelines);

    /// <summary>
    /// The denoised mask shading reads this frame.
    /// </summary>
    public GpuBuffer Mask => _denoiser.Mask;

    /// <summary>
    /// The mask as the rays left it.
    /// </summary>
    public GpuBuffer Traced { get; private set; }

    /// <summary>
    /// A bit for each pixel its sun ray reached the sun from, a word for each 8 by 4 pixels.
    /// </summary>
    public GpuBuffer SunTiles { get; private set; }

    public void Dispose()
    {
        Destroy(null);
        _denoiser.Dispose();
    }

    /// <summary>
    /// The masks made again for a picture of a size; the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height)
    {
        Destroy(graph);
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        Result<GpuBuffer> traced = CreateBuffer("Traced shadows", (long)width * height * BytesPerPixel);
        Result<GpuBuffer> sunTiles = CreateBuffer("Sun shadow tiles", tiles * sizeof(uint));
        Traced = traced.Value;
        SunTiles = sunTiles.Value;
        if (!traced.Ok || !sunTiles.Ok)
            return Result.Failure($"the shadow mask was not made: {(traced.Ok ? sunTiles.Error : traced.Error)}");

        return _denoiser.Resize(width, height);
    }

    /// <summary>
    /// The denoiser's history kept and its constants uploaded, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare() => _denoiser.Prepare();

    /// <summary>
    /// Each pixel's shadows, from the view's surfaces.
    /// </summary>
    public void Record(in RenderView view, in GpuConstants header, GpuBuffer lightingConstants)
    {
        FrameTargets targets = view.Targets;
        _tracePass
            .Run(header)
            .Reads(
                view.Constants,
                lightingConstants,
                lights.Table,
                lights.Tiles,
                view.Instances,
                pools.Clusters,
                pools.Vertices,
                pools.Indices,
                visibility.VisibleClusters,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal
            )
            .Writes(Traced, SunTiles)
            .Dispatch(pipelines.GetTiles(targets.Width), pipelines.GetTiles(targets.Height));
        _denoiser.Record(view, header, lightingConstants, Traced, SunTiles);
    }

    private Result<GpuBuffer> CreateBuffer(string name, long bytes) =>
        gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = bytes,
                Usage = BufferUsages.Storage,
                Name = name,
            }
        );

    private void Destroy(RenderGraph? forgetIn)
    {
        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[Traced, SunTiles])
        {
            if (buffer.IsNone)
                continue;

            forgetIn?.Forget(buffer);
            gpu.Destroy(buffer);
        }

        Traced = GpuBuffer.None;
        SunTiles = GpuBuffer.None;
    }
}
