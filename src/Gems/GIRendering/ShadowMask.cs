using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// A frame's shadow mask and the denoiser's buffers and targets. <paramref name="Mask"/> is the denoised mask shading
/// reads, <paramref name="Traced"/> the mask as the rays left it, <paramref name="SunTiles"/> a bit for each pixel its
/// sun ray reached the sun from, a word for each 8 by 4 pixels, and <paramref name="SunTint"/> the colour the sun's
/// light takes on through glass; the previous ones are what the last frame wrote.
/// </summary>
internal readonly record struct ShadowBuffers(
    GpuBuffer Constants,
    GpuBuffer TileStates,
    GpuBuffer Traced,
    GpuBuffer SunTiles,
    GpuTexture SunTint,
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
internal sealed class ShadowMask(AssetManager assets, GpuResources resources)
{
    private const int BytesPerPixel = 16;
    private const int SunTileWidth = 8;
    private const int SunTileHeight = 4;
    private const int FilterPasses = 3;

    /// <summary>
    /// The twin of <c>DenoiseShadows.slang</c>'s group size, a tile state each group.
    /// </summary>
    private const int DenoiseGroupSize = 8;

    private static readonly string[] _filterPassNames = ["Filter shadows 1", "Filter shadows 2", "Filter shadows 3"];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "TraceShadows", "TraceShadows");
    private readonly ShaderEntry _classifyShader = PassShaders.GetEntry(assets, "DenoiseShadows", "ClassifyShadows");
    private readonly ShaderEntry _filterShader = PassShaders.GetEntry(assets, "DenoiseShadows", "FilterShadows");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "DenoiseShadows", "BlendShadows");

    /// <summary>
    /// The mask the frame drawn last wrote, which the next blends with.
    /// </summary>
    private GpuBuffer _mask;

    /// <summary>
    /// The moments the frame drawn last wrote, which the next blends with.
    /// </summary>
    private GpuTexture _moments;

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _classifyShader, _filterShader, _blendShader];

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
        long groups =
            (long)((width + DenoiseGroupSize - 1) / DenoiseGroupSize)
            * ((height + DenoiseGroupSize - 1) / DenoiseGroupSize);
        RenderGraph graph = view.Graph;
        Result<GpuBuffer> traced = graph.Buffer(GpuDescriptions.GetBuffer("Traced shadows", maskBytes));
        Result<GpuBuffer> sunTiles = graph.Buffer(GpuDescriptions.GetBuffer("Sun shadow tiles", tiles * sizeof(uint)));
        Result<GpuTexture> sunTint = graph.Texture(
            GpuDescriptions.GetTexture("Sun tint", width, height, GpuFormat.R16G16B16A16Sfloat)
        );
        Result<GpuBuffer> constants = graph.Buffer(
            GpuDescriptions.GetBuffer("Shadow denoiser constants", ShadowDenoiseConstants.Bytes)
        );
        Result<GpuBuffer> tileStates = graph.Buffer(
            GpuDescriptions.GetBuffer("Shadow tile states", groups * sizeof(uint))
        );
        Result<GpuBuffer> mask = graph.Buffer(GpuDescriptions.GetBuffer("Shadow mask", maskBytes));
        Result<GpuBuffer> maskHistory = graph.Buffer(GpuDescriptions.GetBuffer("Shadow mask history", maskBytes));
        Result<GpuTexture> sunBlend = graph.Texture(
            GpuDescriptions.GetTexture("Sun shadow blend", width, height, GpuFormat.R16G16Sfloat)
        );
        Result<GpuTexture> sunHistory = graph.Texture(
            GpuDescriptions.GetTexture("Sun shadow history", width, height, GpuFormat.R16G16Sfloat)
        );
        Result<GpuTexture> sunDenoised = graph.Texture(
            GpuDescriptions.GetTexture("Sun shadow", width, height, GpuFormat.R8Unorm)
        );
        Result<GpuTexture> moments = graph.Texture(
            GpuDescriptions.GetTexture("Sun shadow moments", width, height, GpuFormat.R16G16B16A16Sfloat)
        );
        Result<GpuTexture> momentsHistory = graph.Texture(
            GpuDescriptions.GetTexture("Sun shadow moments history", width, height, GpuFormat.R16G16B16A16Sfloat)
        );
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [
                    traced,
                    sunTiles,
                    sunTint,
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
            sunTint.Value,
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
        graph.Upload(shadows.Constants, [denoise]);

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
        in ShadowBuffers shadows,
        GpuBuffer hitMaterials
    )
    {
        view.Graph.Compute("Trace shadows", _traceShader, header)
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
                view.GBufferNormal,
                resources.LeafClusters,
                hitMaterials
            )
            .Writes(shadows.Traced, shadows.SunTiles, shadows.SunTint)
            .DispatchOver(view.Width, view.Height);

        // Denoise: classify the sun's tiles, filter three times, blend with last frame.
        GpuConstants denoise = header with
        {
            Y = shadows.Constants.Id,
        };
        view.Graph.Compute("Classify shadows", _classifyShader, denoise)
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
            view.Graph.Compute(_filterPassNames[pass], _filterShader, denoise with { W = (uint)pass })
                .Reads(view.Constants, shadows.Constants, shadows.TileStates, view.Depth, view.GBufferNormal, input)
                .Writes(output)
                .DispatchOver(view.Width, view.Height);
        }

        view.Graph.Compute("Blend shadows", _blendShader, denoise)
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
