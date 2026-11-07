using Magic.Gpu;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// A frame's shadow mask and the denoiser's buffers and targets. <paramref name="Mask"/> is the denoised mask shading
/// reads, <paramref name="Traced"/> the mask as the rays left it, and <paramref name="SunTiles"/> a bit for each pixel
/// its sun ray reached the sun from, a word for each 8 by 4 pixels; the previous ones are what the last frame wrote.
/// </summary>
internal readonly record struct ShadowBuffers(
    GpuBuffer Constants,
    GpuBuffer TileStates,
    GpuBuffer Traced,
    GpuBuffer SunTiles,
    GpuBuffer Mask,
    GpuBuffer PreviousMask,
    GpuTexture SunBlend,
    GpuTexture SunHistory,
    GpuTexture SunDenoised,
    GpuTexture Moments,
    GpuTexture PreviousMoments
);

/// <summary>
/// On a GPU that traces rays, how much of each light reaches each pixel: a pass that traces a ray a pixel towards the
/// sun and each of the first lights its tile lists, into a buffer of 16 bytes a pixel, the layout
/// <c>Scene/Shadows.slang</c> reads, and the sun's tiles beside it; then the noise taken out, into the mask shading
/// reads: the sun's slot in time and then in space, the tile lights' slots in time alone, since a slot may hold another
/// light in the next tile or frame. It blends with last frame's mask and moments, so it keeps two of each and swaps
/// them every frame, and with none while the view has no history.
/// </summary>
internal sealed class ShadowMask(IGraphicsApi gpu, GpuPipelines pipelines, GpuResources resources)
{
    private const int BytesPerPixel = 16;
    private const int SunTileWidth = 8;
    private const int SunTileHeight = 4;
    private const int FilterPasses = 3;

    private static readonly string[] _filterPassNames = ["Filter shadows 1", "Filter shadows 2", "Filter shadows 3"];

    private readonly GpuPass _tracePass = pipelines.Compute("Trace shadows", "TraceShadows", "TraceShadows");
    private readonly GpuPass _classifyPass = pipelines.Compute("Classify shadows", "DenoiseShadows", "ClassifyShadows");
    private readonly GpuPass _filterPass = pipelines.Compute("Filter shadows", "DenoiseShadows", "FilterShadows");
    private readonly GpuPass _blendPass = pipelines.Compute("Blend shadows", "DenoiseShadows", "BlendShadows");

    /// <summary>
    /// The mask the frame drawn last wrote, which the next blends with.
    /// </summary>
    private GpuBuffer _mask;

    /// <summary>
    /// The moments the frame drawn last wrote, which the next blends with.
    /// </summary>
    private GpuTexture _moments;

    /// <summary>
    /// The masks and the denoiser's targets asked for at the view's size, last frame's mask and moments kept to blend
    /// with, and the denoiser's constants uploaded, in a transfer pass the caller opened.
    /// </summary>
    public Result<ShadowBuffers> Prepare(in RenderView view)
    {
        int width = view.Width;
        int height = view.Height;
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        long maskBytes = (long)width * height * BytesPerPixel;
        (uint groupsX, uint groupsY) = _classifyPass.GetGroups(width, height);
        Result<GpuBuffer> traced = resources.TryGetBuffer("Traced shadows", maskBytes);
        Result<GpuBuffer> sunTiles = resources.TryGetBuffer("Sun shadow tiles", tiles * sizeof(uint));
        Result<GpuBuffer> constants = resources.TryGetBuffer("Shadow denoiser constants", ShadowDenoiseConstants.Bytes);
        Result<GpuBuffer> tileStates = resources.TryGetBuffer(
            "Shadow tile states",
            (long)groupsX * groupsY * sizeof(uint)
        );
        Result<GpuBuffer> mask = resources.TryGetBuffer("Shadow mask", maskBytes);
        Result<GpuBuffer> maskHistory = resources.TryGetBuffer("Shadow mask history", maskBytes);
        Result<GpuTexture> sunBlend = resources.TryGetTexture(
            "Sun shadow blend",
            width,
            height,
            GpuFormat.R16G16Sfloat
        );
        Result<GpuTexture> sunHistory = resources.TryGetTexture(
            "Sun shadow history",
            width,
            height,
            GpuFormat.R16G16Sfloat
        );
        Result<GpuTexture> sunDenoised = resources.TryGetTexture("Sun shadow", width, height, GpuFormat.R8Unorm);
        Result<GpuTexture> moments = resources.TryGetTexture(
            "Sun shadow moments",
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> momentsHistory = resources.TryGetTexture(
            "Sun shadow moments history",
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat
        );
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [
                    traced,
                    sunTiles,
                    constants,
                    tileStates,
                    mask,
                    maskHistory,
                    sunBlend,
                    sunHistory,
                    sunDenoised,
                    moments,
                    momentsHistory,
                ]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        // Last frame's mask and moments are this frame's history.
        (GpuBuffer previousMask, _mask) =
            _mask == mask.Value ? (mask.Value, maskHistory.Value) : (maskHistory.Value, mask.Value);
        (GpuTexture previousMoments, _moments) =
            _moments == moments.Value ? (moments.Value, momentsHistory.Value) : (momentsHistory.Value, moments.Value);
        ShadowBuffers shadows = new(
            constants.Value,
            tileStates.Value,
            traced.Value,
            sunTiles.Value,
            _mask,
            previousMask,
            sunBlend.Value,
            sunHistory.Value,
            sunDenoised.Value,
            _moments,
            previousMoments
        );
        ShadowDenoiseConstants denoise = new()
        {
            TileStates = shadows.TileStates.Id,
            SunBlend = shadows.SunBlend.Id,
            SunHistory = shadows.SunHistory.Id,
            SunDenoised = shadows.SunDenoised.Id,
            Moments = shadows.Moments.Id,
            PreviousMoments = shadows.PreviousMoments.Id,
            PreviousMask = shadows.PreviousMask.Id,
            HasHistory = view.HasHistory ? 1u : 0u,
        };
        view.Graph.Upload(gpu, shadows.Constants, in denoise);

        return Result.Success(shadows);
    }

    /// <summary>
    /// Each pixel's shadows, from the view's surfaces, traced and then denoised.
    /// </summary>
    public void Record(
        in RenderView view,
        in GpuConstants header,
        GpuBuffer lightingConstants,
        in LightBuffers lights,
        in ShadowBuffers shadows
    )
    {
        _tracePass
            .Run(view.Graph, header)
            .Reads(
                view.Constants,
                lightingConstants,
                lights.Table,
                lights.Tiles,
                view.Instances,
                resources.ClusterPool,
                resources.VertexPool,
                resources.IndexPool,
                view.Cull.VisibleClusters,
                view.Visibility,
                view.Depth,
                view.GBufferNormal
            )
            .Writes(shadows.Traced, shadows.SunTiles)
            .DispatchOver(view.Width, view.Height);

        // Denoise: classify the sun's tiles, filter three times, blend with last frame.
        GpuConstants denoise = header with
        {
            Y = shadows.Constants.Id,
        };
        _classifyPass
            .Run(view.Graph, denoise)
            .Reads(
                view.Constants,
                lightingConstants,
                shadows.Constants,
                shadows.SunTiles,
                view.Depth,
                view.GBufferNormal,
                view.Velocity,
                shadows.SunHistory,
                shadows.PreviousMoments
            )
            .Writes(shadows.TileStates, shadows.SunBlend, shadows.Moments)
            .DispatchOver(view.Width, view.Height);

        // Each pass reads the last one's output; the first pass's is what the next frame blends with.
        for (int pass = 0; pass < FilterPasses; pass++)
        {
            (GpuTexture input, GpuTexture output) = pass switch
            {
                0 => (shadows.SunBlend, shadows.SunHistory),
                1 => (shadows.SunHistory, shadows.SunBlend),
                _ => (shadows.SunBlend, shadows.SunDenoised),
            };
            _filterPass
                .Run(view.Graph, denoise with { W = (uint)pass }, _filterPassNames[pass])
                .Reads(view.Constants, shadows.Constants, shadows.TileStates, view.Depth, view.GBufferNormal, input)
                .Writes(output)
                .DispatchOver(view.Width, view.Height);
        }

        _blendPass
            .Run(view.Graph, denoise)
            .Reads(
                view.Constants,
                lightingConstants,
                shadows.Constants,
                shadows.Traced,
                view.Depth,
                view.Velocity,
                shadows.SunDenoised,
                shadows.PreviousMask,
                shadows.PreviousMoments
            )
            .Writes(shadows.Mask)
            .DispatchOver(view.Width, view.Height);
    }
}
