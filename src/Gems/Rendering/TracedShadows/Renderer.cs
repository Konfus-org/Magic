using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.TracedShadows;

/// <summary>
/// How much of each light reaches each pixel, traced through the world as rays see it at the size its settings scale
/// the picture to, then denoised, as <c>DenoiseShadows.slang</c> says. Each frame it writes the ids of the mask and the
/// sun's tint into the frame's buffer and makes the frame's <see cref="ShadowMask"/>.
/// </summary>
public sealed class Renderer(TracedShadowsSettings settings, AssetManager assets) : IRender<TracedInput, ShadowMask>
{
    private const int BytesPerPixel = 16;
    private const int SunTileWidth = 8;
    private const int SunTileHeight = 4;

    /// <summary>
    /// The twin of <c>DenoiseShadows.slang</c>'s group size, a tile state each group.
    /// </summary>
    private const int DenoiseGroupSize = 8;

    private static readonly string[] _filterPassNames = ["Filter shadows 1", "Filter shadows 2"];

    /// <summary>
    /// The two masks and the two moments, the even frames writing the first of each and the odd the second.
    /// </summary>
    private static readonly string[] _maskNames = ["Traced shadow mask 1", "Traced shadow mask 2"];

    private static readonly string[] _momentNames = ["Sun shadow moments 1", "Sun shadow moments 2"];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "TraceShadows", "TraceShadows");
    private readonly ShaderEntry _classifyShader = PassShaders.GetEntry(assets, "DenoiseShadows", "ClassifyShadows");
    private readonly ShaderEntry _filterShader = PassShaders.GetEntry(assets, "DenoiseShadows", "FilterShadows");
    private readonly ShaderEntry _lastFilterShader = PassShaders.GetEntry(
        assets,
        "DenoiseShadows",
        "FilterLastAndBlendShadows"
    );
    private readonly ShaderEntry _upsampleShader = PassShaders.GetEntry(assets, "DenoiseShadows", "UpsampleShadows");

    /// <summary>
    /// The size the frame drawn last traced at, which the next blends with only at the same size.
    /// </summary>
    private (int Width, int Height) _traced;

    /// <summary>
    /// The frame's shadows, traced and denoised, their ids written into the frame's buffer; none while it is off,
    /// the passes' shaders build, or what they write is not made.
    /// </summary>
    public ShadowMask? Render(in Frame frame, RenderGraph graph, in TracedInput input)
    {
        // Off, the history starts afresh once it is back on.
        if (!settings.IsTracedShadowsOn)
        {
            _traced = default;

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState state = graph.GetState(
            _traceShader,
            _classifyShader,
            _filterShader,
            _lastFilterShader,
            _upsampleShader
        );
        if (state != GpuState.Ready)
            return null;

        // The masks and the denoiser's targets at the traced size, this frame's and last frame's mask and moments by
        // the frame's index, and the mask and tint shading reads at the view's size.
        GpuView view = input.View;
        (int width, int height) = view.Scaled(settings.ShadowScale);
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        long maskBytes = (long)width * height * BytesPerPixel;
        long groups =
            (long)((width + DenoiseGroupSize - 1) / DenoiseGroupSize)
            * ((height + DenoiseGroupSize - 1) / DenoiseGroupSize);
        int written = (int)(frame.Index % 2);
        Result<GpuBuffer> constants = graph.Buffer(
            new("Shadow denoiser constants", Unsafe.SizeOf<ShadowDenoiseConstants>())
        );
        Result<GpuBuffer> tileStates = graph.Buffer(new("Shadow tile states", groups * sizeof(uint)));
        Result<GpuBuffer> sunTiles = graph.Buffer(new("Sun shadow tiles", tiles * sizeof(uint)));
        Result<GpuBuffer> tracedMask = graph.Buffer(new(_maskNames[written], maskBytes));
        Result<GpuBuffer> previousMask = graph.Buffer(new(_maskNames[1 - written], maskBytes));
        // Without glass, which the view has targets for only while a material draws translucent, the sun's light stays
        // white, so no tint is asked for and the graph frees the last one's.
        bool hasGlass = !view.GlassDepth.IsNone;
        Result<GpuTexture> tracedTint = hasGlass
            ? GetTracedTexture("Traced sun tint", GpuFormat.R16G16B16A16Sfloat)
            : Result.Success(GpuTexture.None);
        Result<GpuTexture> sunBlend = GetTracedTexture("Sun shadow blend", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunHistory = GetTracedTexture("Sun shadow history", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> moments = GetTracedTexture(_momentNames[written], GpuFormat.R16G16Sfloat);
        Result<GpuTexture> previousMoments = GetTracedTexture(_momentNames[1 - written], GpuFormat.R16G16Sfloat);
        // Traced at the picture's size, the traced mask and tint are what shading reads.
        bool isPictureSize = (width, height) == (view.Width, view.Height);
        Result<GpuBuffer> mask = isPictureSize
            ? tracedMask
            : graph.Buffer(new("Shadow mask", (long)view.Width * view.Height * BytesPerPixel));
        Result<GpuTexture> sunTint =
            isPictureSize || !hasGlass
                ? tracedTint
                : graph.Texture(new("Sun tint", view.Width, view.Height, GpuFormat.R16G16B16A16Sfloat));
        Result asked = Result.FindFailure(
            constants,
            tileStates,
            sunTiles,
            tracedMask,
            previousMask,
            tracedTint,
            sunBlend,
            sunHistory,
            moments,
            previousMoments,
            mask,
            sunTint
        );
        if (!asked.Ok)
        {
            Log.Error($"The shadows are not traced: {asked.Error}");

            return null;
        }

        // Last frame's mask and moments are this frame's history, at the same size only.
        bool hasHistory = view.HasHistory && _traced == (width, height);
        _traced = (width, height);
        ShadowDenoiseConstants denoise = new()
        {
            TileStates = tileStates.Value.Id,
            SunTiles = sunTiles.Value.Id,
            SunBlend = sunBlend.Value.Id,
            SunHistory = sunHistory.Value.Id,
            Moments = moments.Value.Id,
            PreviousMoments = previousMoments.Value.Id,
            Mask = tracedMask.Value.Id,
            PreviousMask = previousMask.Value.Id,
            SunTint = tracedTint.Value.Id,
            HasHistory = hasHistory ? 1u : 0u,
            Width = (uint)width,
            Height = (uint)height,
        };
        graph.Upload(constants.Value, [denoise]);
        GpuShadows row = new() { Mask = mask.Value.Id, SunTint = sunTint.Value.Id };
        graph.Upload(view.Frame, [row], GpuShadows.Offset);

        // Trace each traced pixel's shadows from the view's surfaces.
        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        graph
            .Compute("Trace shadows", _traceShader, header, RenderStages.Light)
            .Reads(
                view.Frame,
                constants.Value,
                view.LightTable,
                view.LightTiles,
                view.Instances,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                view.Meshes,
                input.Traced.HitMaterials
            )
            .Writes(tracedMask.Value, sunTiles.Value, tracedTint.Value)
            .DispatchOver(width, height);

        // Denoise: classify the sun's tiles, filter once or twice, as the settings say, then once more as the mask blends
        // with last frame's.
        graph
            .Compute("Classify shadows", _classifyShader, header, RenderStages.Light)
            .Reads(
                view.Frame,
                constants.Value,
                sunTiles.Value,
                view.Depth,
                view.GBufferNormal,
                view.Velocity,
                sunHistory.Value,
                previousMoments.Value
            )
            .Writes(tileStates.Value, sunBlend.Value, moments.Value)
            .DispatchOver(width, height);

        // Each pass reads the last one's output; the first pass's is what the next frame blends with.
        int passes = Math.Clamp(settings.ShadowFilterPasses, 1, _filterPassNames.Length);
        GpuTexture filtered = passes == 1 ? sunHistory.Value : sunBlend.Value;
        for (int pass = 0; pass < passes; pass++)
        {
            (GpuTexture source, GpuTexture output) =
                pass == 0 ? (sunBlend.Value, sunHistory.Value) : (sunHistory.Value, sunBlend.Value);
            graph
                .Compute(_filterPassNames[pass], _filterShader, header with { W = (uint)pass }, RenderStages.Light)
                .Reads(view.Frame, constants.Value, tileStates.Value, view.Depth, view.GBufferNormal, source)
                .Writes(output)
                .DispatchOver(width, height);
        }

        // The sun's slot filtered a last time from the last pass's into the mask the rays wrote, and the tile lights'
        // slots blended with last frame's.
        graph
            .Compute(
                "Filter last and blend shadows",
                _lastFilterShader,
                header with
                {
                    W = filtered.Id,
                },
                RenderStages.Light
            )
            .Reads(
                view.Frame,
                constants.Value,
                tileStates.Value,
                view.Depth,
                view.GBufferNormal,
                view.Velocity,
                filtered,
                previousMask.Value,
                previousMoments.Value
            )
            .Writes(tracedMask.Value)
            .DispatchOver(width, height);

        // The mask and the sun's tint at the picture's size, each pixel read from the traced pixels on its surface.
        if (!isPictureSize)
        {
            graph
                .Compute("Upsample shadows", _upsampleShader, header, RenderStages.Light)
                .Reads(
                    view.Frame,
                    constants.Value,
                    view.Depth,
                    view.GBufferNormal,
                    view.GBufferSurface,
                    tracedMask.Value,
                    tracedTint.Value
                )
                .Writes(mask.Value, sunTint.Value)
                .DispatchOver(view.Width, view.Height);
        }

        return new ShadowMask(mask.Value, sunTint.Value);

        // A texture at the traced size.
        Result<GpuTexture> GetTracedTexture(string name, GpuFormat format) =>
            graph.Texture(new(name, width, height, format));
    }
}

/// <summary>
/// The heap ids of the shadow denoiser's targets and the traced size, the twin of <c>DenoiseShadows.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowDenoiseConstants
{
    public uint TileStates;

    public uint SunTiles;

    public uint SunBlend;

    public uint SunHistory;

    public uint Moments;

    public uint PreviousMoments;

    /// <summary>
    /// The traced mask, the rays' and then the denoised, last frame's, and the sun's tint as traced.
    /// </summary>
    public uint Mask;

    public uint PreviousMask;

    public uint SunTint;

    /// <summary>
    /// 0 on the first frame at a size, when last frame's targets hold nothing to blend with.
    /// </summary>
    public uint HasHistory;

    /// <summary>
    /// The traced size.
    /// </summary>
    public uint Width;

    public uint Height;
}
