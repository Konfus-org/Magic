using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// On a GPU that traces rays, how much of each light reaches each pixel: a pass that traces a ray a pixel towards the
/// sun and each of the first lights its tile lists, into a buffer of 16 bytes a pixel, the layout
/// <c>Scene/Shadows.slang</c> reads.
/// </summary>
internal sealed class ShadowMask(IGraphicsApi gpu, RenderGraph graph, GpuPipelines pipelines, LightTiles lights)
    : IDisposable
{
    private const int BytesPerPixel = 16;

    private readonly GpuPass _tracePass = pipelines.Compute(
        "Trace shadows",
        "TraceShadows",
        "TraceShadows",
        isTiled: true
    );

    public GpuBuffer Mask { get; private set; }

    public void Dispose()
    {
        if (!Mask.IsNone)
            gpu.Destroy(Mask);
    }

    /// <summary>
    /// The mask made again for a picture of a size; the old one destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height)
    {
        if (!Mask.IsNone)
        {
            graph.Forget(Mask);
            gpu.Destroy(Mask);
            Mask = GpuBuffer.None;
        }

        Result<GpuBuffer> mask = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)width * height * BytesPerPixel,
                Usage = BufferUsages.Storage,
                Name = "Shadow mask",
            }
        );
        if (!mask.Ok)
            return Result.Failure($"the shadow mask was not made: {mask.Error}");

        Mask = mask.Value;

        return Result.Success();
    }

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
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal
            )
            .Writes(Mask)
            .Dispatch(pipelines.GetTiles(targets.Width), pipelines.GetTiles(targets.Height));
    }
}
