using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.TracedShadows;

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
/// How much of each light reaches each pixel, traced through the world as rays see it: a pass that traces a ray a pixel
/// towards the sun and each of the first lights its tile lists, into a buffer of 16 bytes a pixel, the layout
/// <c>Scene/Shadows.slang</c> reads, and the sun's tiles beside it; then the noise taken out, into the mask shading
/// reads: the sun's slot in time and then in space, the tile lights' slots in time alone, since a slot may hold another
/// light in the next tile or frame. It blends with last frame's mask and moments, so it keeps two of each and swaps
/// them every frame, and with none while the view has no history. Each frame it writes the masks' ids into the frame's
/// buffer and makes the frame's <see cref="ShadowMask"/>. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class TracedShadowsGem(AssetManager assets) : IGem, IRender<TracedInput, ShadowMask>
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

    private readonly ShaderEntry _traceShader = GetEntry(assets, "TraceShadows", "TraceShadows");
    private readonly ShaderEntry _classifyShader = GetEntry(assets, "DenoiseShadows", "ClassifyShadows");
    private readonly ShaderEntry _filterShader = GetEntry(assets, "DenoiseShadows", "FilterShadows");
    private readonly ShaderEntry _blendShader = GetEntry(assets, "DenoiseShadows", "BlendShadows");

    /// <summary>
    /// The mask the frame drawn last wrote, which the next blends with.
    /// </summary>
    private GpuBuffer _mask;

    /// <summary>
    /// The moments the frame drawn last wrote, which the next blends with.
    /// </summary>
    private GpuTexture _moments;

    /// <summary>
    /// The frame's shadows, traced and denoised, their ids written into the frame's buffer; none while the passes'
    /// shaders are built or what they write is not made.
    /// </summary>
    public ShadowMask? Render(in Frame frame, RenderGraph graph, in TracedInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each, so each is asked.
        bool isBuilt = graph.GetState(_traceShader) == GpuState.Ready;
        isBuilt &= graph.GetState(_classifyShader) == GpuState.Ready;
        isBuilt &= graph.GetState(_filterShader) == GpuState.Ready;
        isBuilt &= graph.GetState(_blendShader) == GpuState.Ready;
        if (!isBuilt)
            return null;

        Result<ShadowBuffers> made = Prepare(graph, input.View);
        if (!made.Ok)
        {
            Log.Error($"The shadows are not traced: {made.Error}");

            return null;
        }

        ShadowBuffers shadows = made.Value;
        GpuShadows row = new()
        {
            Mask = shadows.Mask.Id,
            Traced = shadows.Traced.Id,
            SunTiles = shadows.SunTiles.Id,
            SunTint = shadows.SunTint.Id,
        };
        graph.Upload(input.View.Frame, [row], GpuShadows.Offset);
        Record(graph, input, shadows);

        return new ShadowMask(shadows.Mask, shadows.SunTint);
    }

    private static ShaderEntry GetEntry(AssetManager assets, string module, string name) =>
        new()
        {
            Module = assets.Find<Shader>($"Shaders/{module}.slang"),
            Name = name,
            Stage = ShaderStage.Compute,
        };

    /// <summary>
    /// The masks and the denoiser's targets asked for at the view's size, last frame's mask and moments kept to blend
    /// with, and the denoiser's constants uploaded.
    /// </summary>
    private Result<ShadowBuffers> Prepare(RenderGraph graph, in GpuView view)
    {
        int width = view.Width;
        int height = view.Height;
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        long maskBytes = (long)width * height * BytesPerPixel;
        long groups =
            (long)((width + DenoiseGroupSize - 1) / DenoiseGroupSize)
            * ((height + DenoiseGroupSize - 1) / DenoiseGroupSize);
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
    private void Record(RenderGraph graph, in TracedInput input, in ShadowBuffers shadows)
    {
        GpuView view = input.View;
        GpuConstants header = new(view.Frame.Id);
        graph
            .Compute("Trace shadows", _traceShader, header, RenderStage.Light)
            .Reads(
                view.Frame,
                view.LightTable,
                view.LightTiles,
                view.Instances,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                view.Meshes,
                input.Traced.HitMaterials
            )
            .Writes(shadows.Traced, shadows.SunTiles, shadows.SunTint)
            .DispatchOver(view.Width, view.Height);

        // Denoise: classify the sun's tiles, filter three times, blend with last frame.
        GpuConstants denoise = header with
        {
            Y = shadows.Constants.Id,
        };
        graph
            .Compute("Classify shadows", _classifyShader, denoise, RenderStage.Light)
            .Reads(
                view.Frame,
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
            (GpuTexture source, GpuTexture output) = pass switch
            {
                0 => (shadows.SunBlend, shadows.SunHistory),
                1 => (shadows.SunHistory, shadows.SunBlend),
                _ => (shadows.SunBlend, shadows.SunDenoised),
            };
            graph
                .Compute(_filterPassNames[pass], _filterShader, denoise with { W = (uint)pass }, RenderStage.Light)
                .Reads(view.Frame, shadows.Constants, shadows.TileStates, view.Depth, view.GBufferNormal, source)
                .Writes(output)
                .DispatchOver(view.Width, view.Height);
        }

        graph
            .Compute("Blend shadows", _blendShader, denoise, RenderStage.Light)
            .Reads(
                view.Frame,
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

/// <summary>
/// The heap ids of the shadow denoiser's targets, the twin of <c>DenoiseShadows.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowDenoiseConstants
{
    public const int Bytes = 32;

    public uint TileStates;

    public uint SunBlend;

    public uint SunHistory;

    public uint SunDenoised;

    public uint Moments;

    public uint PreviousMoments;

    public uint PreviousMask;

    /// <summary>
    /// 0 on the first frame at a size, when last frame's targets hold nothing to blend with.
    /// </summary>
    public uint HasHistory;
}
