using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// On a GPU that traces rays, how much of each light reaches each pixel: a pass that traces a ray a pixel towards the
/// sun and each of the first lights its tile lists, into a buffer of 16 bytes a pixel, the layout
/// <c>Scene/Shadows.slang</c> reads, and the sun's tiles beside it; then the noise taken out, into the mask shading
/// reads: the sun's slot in time and then in space, the tile lights' slots in time alone, since a slot may hold another
/// light in the next tile or frame. It blends with last frame's mask and moments, so it keeps two of each and swaps
/// them every frame.
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
    private const int FilterPasses = 3;

    private static readonly string[] _filterPassNames = ["Filter shadows 1", "Filter shadows 2", "Filter shadows 3"];

    private readonly GpuPass _tracePass = pipelines.Compute("Trace shadows", "TraceShadows", "TraceShadows");
    private readonly GpuPass _classifyPass = pipelines.Compute("Classify shadows", "DenoiseShadows", "ClassifyShadows");
    private readonly GpuPass _filterPass = pipelines.Compute("Filter shadows", "DenoiseShadows", "FilterShadows");
    private readonly GpuPass _blendPass = pipelines.Compute("Blend shadows", "DenoiseShadows", "BlendShadows");

    private GpuBuffer _constants;
    private GpuBuffer _tileStates;
    private GpuBuffer _previousMask;
    private GpuTexture _sunBlend;
    private GpuTexture _sunHistory;
    private GpuTexture _sunDenoised;
    private GpuTexture _moments;
    private GpuTexture _previousMoments;
    private bool _hasHistory;

    /// <summary>
    /// The denoised mask shading reads this frame.
    /// </summary>
    public GpuBuffer Mask { get; private set; }

    /// <summary>
    /// The mask as the rays left it.
    /// </summary>
    public GpuBuffer Traced { get; private set; }

    /// <summary>
    /// A bit for each pixel its sun ray reached the sun from, a word for each 8 by 4 pixels.
    /// </summary>
    public GpuBuffer SunTiles { get; private set; }

    public void Dispose() => Destroy();

    /// <summary>
    /// Forgets the shadows of frames gone, so none blend into what is drawn next.
    /// </summary>
    public void ClearHistory() => _hasHistory = false;

    /// <summary>
    /// The masks and the denoiser's targets made again for a picture of a size, with no history; the old ones destroyed
    /// and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height)
    {
        Destroy();
        _hasHistory = false;
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        long maskBytes = (long)width * height * BytesPerPixel;
        (uint groupsX, uint groupsY) = _classifyPass.GetGroups(width, height);
        Result<GpuBuffer> traced = gpu.CreateBuffer("Traced shadows", maskBytes);
        Result<GpuBuffer> sunTiles = gpu.CreateBuffer("Sun shadow tiles", tiles * sizeof(uint));
        Result<GpuBuffer> constants = gpu.CreateBuffer("Shadow denoiser constants", ShadowDenoiseConstants.Bytes);
        Result<GpuBuffer> tileStates = gpu.CreateBuffer("Shadow tile states", (long)groupsX * groupsY * sizeof(uint));
        Result<GpuBuffer> mask = gpu.CreateBuffer("Shadow mask", maskBytes);
        Result<GpuBuffer> previousMask = gpu.CreateBuffer("Shadow mask history", maskBytes);
        Result<GpuTexture> sunBlend = gpu.CreateTexture("Sun shadow blend", width, height, GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunHistory = gpu.CreateTexture("Sun shadow history", width, height, GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunDenoised = gpu.CreateTexture("Sun shadow", width, height, GpuFormat.R8Unorm);
        Result<GpuTexture> moments = gpu.CreateTexture(
            "Sun shadow moments",
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> previousMoments = gpu.CreateTexture(
            "Sun shadow moments history",
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat
        );
        Traced = traced.Value;
        SunTiles = sunTiles.Value;
        _constants = constants.Value;
        _tileStates = tileStates.Value;
        Mask = mask.Value;
        _previousMask = previousMask.Value;
        _sunBlend = sunBlend.Value;
        _sunHistory = sunHistory.Value;
        _sunDenoised = sunDenoised.Value;
        _moments = moments.Value;
        _previousMoments = previousMoments.Value;
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [
                    traced,
                    sunTiles,
                    constants,
                    tileStates,
                    mask,
                    previousMask,
                    sunBlend,
                    sunHistory,
                    sunDenoised,
                    moments,
                    previousMoments,
                ]
        )
        {
            if (!made.Ok)
                return made;
        }

        return Result.Success();
    }

    /// <summary>
    /// Last frame's mask and moments kept to blend with, and the denoiser's constants uploaded, in a transfer pass the
    /// caller opened.
    /// </summary>
    public void Prepare()
    {
        (Mask, _previousMask) = (_previousMask, Mask);
        (_moments, _previousMoments) = (_previousMoments, _moments);
        ShadowDenoiseConstants constants = new()
        {
            TileStates = _tileStates.Id,
            SunBlend = _sunBlend.Id,
            SunHistory = _sunHistory.Id,
            SunDenoised = _sunDenoised.Id,
            Moments = _moments.Id,
            PreviousMoments = _previousMoments.Id,
            PreviousMask = _previousMask.Id,
            HasHistory = _hasHistory ? 1u : 0u,
        };
        _hasHistory = true;
        GpuUpload upload = gpu.BeginUpload(ShadowDenoiseConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(_constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, _constants, 0, ShadowDenoiseConstants.Bytes);
    }

    /// <summary>
    /// Each pixel's shadows, from the view's surfaces, traced and then denoised.
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
            .DispatchOver(targets.Width, targets.Height);

        // Denoise: classify the sun's tiles, filter three times, blend with last frame.
        GpuConstants denoise = header with
        {
            Y = _constants.Id,
        };
        _classifyPass
            .Run(denoise)
            .Reads(
                view.Constants,
                lightingConstants,
                _constants,
                SunTiles,
                targets.Depth,
                targets.GBufferNormal,
                targets.Velocity,
                _sunHistory,
                _previousMoments
            )
            .Writes(_tileStates, _sunBlend, _moments)
            .DispatchOver(targets.Width, targets.Height);

        // Each pass reads the last one's output; the first pass's is what the next frame blends with.
        for (int pass = 0; pass < FilterPasses; pass++)
        {
            (GpuTexture input, GpuTexture output) = pass switch
            {
                0 => (_sunBlend, _sunHistory),
                1 => (_sunHistory, _sunBlend),
                _ => (_sunBlend, _sunDenoised),
            };
            _filterPass
                .Run(denoise with { W = (uint)pass }, _filterPassNames[pass])
                .Reads(view.Constants, _constants, _tileStates, targets.Depth, targets.GBufferNormal, input)
                .Writes(output)
                .DispatchOver(targets.Width, targets.Height);
        }

        _blendPass
            .Run(denoise)
            .Reads(
                view.Constants,
                lightingConstants,
                _constants,
                Traced,
                targets.Depth,
                targets.Velocity,
                _sunDenoised,
                _previousMask,
                _previousMoments
            )
            .Writes(Mask)
            .DispatchOver(targets.Width, targets.Height);
    }

    private void Destroy()
    {
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [Traced, SunTiles, _constants, _tileStates, Mask, _previousMask]
        )
        {
            graph.Forget(buffer);
            gpu.Destroy(buffer);
        }

        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [_sunBlend, _sunHistory, _sunDenoised, _moments, _previousMoments]
        )
        {
            graph.Forget(texture);
            gpu.Destroy(texture);
        }

        Traced = GpuBuffer.None;
        SunTiles = GpuBuffer.None;
        _constants = GpuBuffer.None;
        _tileStates = GpuBuffer.None;
        Mask = GpuBuffer.None;
        _previousMask = GpuBuffer.None;
        _sunBlend = GpuTexture.None;
        _sunHistory = GpuTexture.None;
        _sunDenoised = GpuTexture.None;
        _moments = GpuTexture.None;
        _previousMoments = GpuTexture.None;
    }
}
