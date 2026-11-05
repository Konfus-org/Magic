using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The traced shadow mask's noise taken out, into the mask shading reads: the sun's slot in time and then in space, the
/// tile lights' slots in time alone, since a slot may hold another light in the next tile or frame. It blends with last
/// frame's mask and moments, so it keeps two of each and swaps them every frame.
/// </summary>
internal sealed class ShadowDenoiser(IGraphicsApi gpu, RenderGraph graph, GpuPipelines pipelines) : IDisposable
{
    private const int MaskBytesPerPixel = 16;
    private const int FilterPasses = 3;

    private static readonly string[] _filterPassNames = ["Filter shadows 1", "Filter shadows 2", "Filter shadows 3"];

    private readonly GpuPass _classifyPass = pipelines.Compute(
        "Classify shadows",
        "DenoiseShadows",
        "ClassifyShadows",
        isTiled: true
    );

    private readonly GpuPass _filterPass = pipelines.Compute(
        "Filter shadows",
        "DenoiseShadows",
        "FilterShadows",
        isTiled: true
    );

    private readonly GpuPass _blendPass = pipelines.Compute(
        "Blend shadows",
        "DenoiseShadows",
        "BlendShadows",
        isTiled: true
    );

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
    /// The mask shading reads this frame.
    /// </summary>
    public GpuBuffer Mask { get; private set; }

    public void Dispose() => Destroy(null);

    /// <summary>
    /// The targets made again for a picture of a size, with no history; the old ones destroyed and forgotten by the
    /// graph.
    /// </summary>
    public Result Resize(int width, int height)
    {
        Destroy(graph);
        _hasHistory = false;
        long maskBytes = (long)width * height * MaskBytesPerPixel;
        long groups = (long)pipelines.GetTiles(width) * pipelines.GetTiles(height);
        Result<GpuBuffer> constants = CreateBuffer("Shadow denoiser constants", ShadowDenoiseConstants.Bytes);
        Result<GpuBuffer> tileStates = CreateBuffer("Shadow tile states", groups * sizeof(uint));
        Result<GpuBuffer> mask = CreateBuffer("Shadow mask", maskBytes);
        Result<GpuBuffer> previousMask = CreateBuffer("Shadow mask history", maskBytes);
        Result<GpuTexture> sunBlend = CreateTexture("Sun shadow blend", width, height, GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunHistory = CreateTexture("Sun shadow history", width, height, GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunDenoised = CreateTexture("Sun shadow", width, height, GpuFormat.R8Unorm);
        Result<GpuTexture> moments = CreateTexture("Sun shadow moments", width, height, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> previousMoments = CreateTexture(
            "Sun shadow moments history",
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat
        );
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
                [constants, tileStates, mask, previousMask, sunBlend, sunHistory, sunDenoised, moments, previousMoments]
        )
        {
            if (!made.Ok)
                return Result.Failure($"a shadow denoiser target was not made: {made.Error}");
        }

        return Result.Success();
    }

    /// <summary>
    /// Last frame's mask and moments kept to blend with, and the constants uploaded, in a transfer pass the caller
    /// opened.
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
    /// The mask from the traced one and the sun's tiles.
    /// </summary>
    public void Record(
        in RenderView view,
        in GpuConstants header,
        GpuBuffer lightingConstants,
        GpuBuffer traced,
        GpuBuffer sunTiles
    )
    {
        FrameTargets targets = view.Targets;
        GpuConstants denoise = header with { Y = _constants.Id };
        uint groupsX = pipelines.GetTiles(targets.Width);
        uint groupsY = pipelines.GetTiles(targets.Height);
        _classifyPass
            .Run(denoise)
            .Reads(
                view.Constants,
                lightingConstants,
                _constants,
                sunTiles,
                targets.Depth,
                targets.GBufferNormal,
                targets.Velocity,
                _sunHistory,
                _previousMoments
            )
            .Writes(_tileStates, _sunBlend, _moments)
            .Dispatch(groupsX, groupsY);

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
                .Dispatch(groupsX, groupsY);
        }

        _blendPass
            .Run(denoise)
            .Reads(
                view.Constants,
                lightingConstants,
                _constants,
                traced,
                targets.Depth,
                targets.Velocity,
                _sunDenoised,
                _previousMask,
                _previousMoments
            )
            .Writes(Mask)
            .Dispatch(groupsX, groupsY);
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

    private Result<GpuTexture> CreateTexture(string name, int width, int height, GpuFormat format) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = width,
                Height = height,
                Format = format,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Name = name,
            }
        );

    private void Destroy(RenderGraph? forgetIn)
    {
        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[_constants, _tileStates, Mask, _previousMask])
        {
            if (buffer.IsNone)
                continue;

            forgetIn?.Forget(buffer);
            gpu.Destroy(buffer);
        }

        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [_sunBlend, _sunHistory, _sunDenoised, _moments, _previousMoments]
        )
        {
            if (texture.IsNone)
                continue;

            forgetIn?.Forget(texture);
            gpu.Destroy(texture);
        }

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
