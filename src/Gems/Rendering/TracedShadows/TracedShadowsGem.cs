using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.TracedShadows;

/// <summary>
/// A frame's shadow mask and the denoiser's buffers and targets, all at the traced size but <paramref name="Mask"/> and
/// <paramref name="SunTint"/>, at the picture's, which shading reads. <paramref name="TracedMask"/> is the mask as the
/// rays left it, then denoised in place, <paramref name="SunTiles"/> a bit for each pixel its sun ray reached the sun
/// from, a word for each 8 by 4 pixels, and <paramref name="TracedTint"/> the colour the sun's light takes on through
/// glass, both tints none while the view draws no glass; the previous ones are what the last frame wrote.
/// </summary>
internal readonly record struct ShadowBuffers(
    GpuBuffer Constants,
    GpuBuffer TileStates,
    GpuBuffer SunTiles,
    GpuTexture TracedTint,
    GpuBuffer TracedMask,
    GpuBuffer PreviousMask,
    GpuTexture SunBlend,
    GpuTexture SunHistory,
    GpuTexture Moments,
    GpuTexture PreviousMoments,
    GpuBuffer Mask,
    GpuTexture SunTint
);

/// <summary>
/// How much of each light reaches each pixel, traced through the world as rays see it at the size its settings scale
/// the picture to: a pass that traces a ray a pixel towards the sun and each of the first lights its tile lists, into a
/// mask of 16 bytes a pixel, the layout <c>Scene/Shadows.slang</c> reads, and the sun's tiles beside it; then the noise
/// taken out of the mask in place: the sun's slot in time and then in space, the tile lights' slots in time alone,
/// since a slot may hold another light in the next tile or frame; then the mask and the sun's tint brought to the
/// picture's size along its surfaces. It blends with last frame's mask and moments, so it keeps two of each, the even
/// frames writing the first and the odd the second, and blends with none while the view has no history at this size.
/// Each frame it writes the ids of the mask, the sun's tiles and its tint into the frame's buffer and makes the frame's
/// <see cref="ShadowMask"/>. It is public because nothing in its assembly makes it: Magic does, by reflection, when a
/// project lists the gem.
/// </summary>
public sealed class TracedShadowsGem(TracedShadowsSettings settings, AssetManager assets, IShaderCompiler shaders)
    : IGem,
        IHasSettings<TracedShadowsSettings>,
        IRender<TracedInput, ShadowMask>
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

    private readonly ShaderEntry _traceShader = GetEntry(assets, "TraceShadows", "TraceShadows");
    private readonly ShaderEntry _classifyShader = GetEntry(assets, "DenoiseShadows", "ClassifyShadows");
    private readonly ShaderEntry _filterShader = GetEntry(assets, "DenoiseShadows", "FilterShadows");
    private readonly ShaderEntry _blendShader = GetEntry(assets, "DenoiseShadows", "BlendShadows");
    private readonly ShaderEntry _upsampleShader = GetEntry(assets, "DenoiseShadows", "UpsampleShadows");

    /// <summary>
    /// Each material shader's <c>Translucent</c> constant, reflected the first time a view draws with it.
    /// </summary>
    private readonly Dictionary<ShaderTypeArgument, Task<Result<long>>> _translucent = [];

    /// <summary>
    /// The size the frame drawn last traced at, which the next blends with only at the same size.
    /// </summary>
    private (int Width, int Height) _traced;

    /// <summary>
    /// How the shadows are traced, which <see cref="Render"/> reads whole each frame. Game thread.
    /// </summary>
    public TracedShadowsSettings Settings { get; set; } = settings;

    /// <summary>
    /// The frame's shadows, traced and denoised, their ids written into the frame's buffer; none while the passes'
    /// shaders are built or what they write is not made.
    /// </summary>
    public ShadowMask? Render(in Frame frame, RenderGraph graph, in TracedInput input)
    {
        // Off, the history starts afresh once it is back on.
        if (!Settings.IsTracedShadowsOn)
        {
            _traced = default;

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState state = graph.GetState(_traceShader, _classifyShader, _filterShader, _blendShader, _upsampleShader);
        if (state != GpuState.Ready)
            return null;

        Result<ShadowBuffers> made = Prepare(graph, input.View, frame.Index);
        if (!made.Ok)
        {
            Log.Error($"The shadows are not traced: {made.Error}");

            return null;
        }

        ShadowBuffers shadows = made.Value;
        GpuShadows row = new()
        {
            Mask = shadows.Mask.Id,
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
    /// A side of the picture at <paramref name="scale"/>, from 1 pixel up to the whole side.
    /// </summary>
    private static int Scale(int side, float scale) => Math.Clamp((int)MathF.Round(side * scale), 1, side);

    /// <summary>
    /// Whether the view draws with a translucent material shader, as glass is, whose colour the sun's light takes on
    /// through it. A shader counts as opaque until its constant is reflected.
    /// </summary>
    private bool HasGlass(in GpuView view)
    {
        bool hasGlass = false;
        for (int i = 0; i < view.MaterialShaders.Count; i++)
        {
            ShaderTypeArgument shader = view.MaterialShaders[i];
            if (!_translucent.TryGetValue(shader, out Task<Result<long>>? translucent))
            {
                translucent = shaders.ReflectConstantAsync(shader, "Translucent", CancellationToken.None);
                _translucent.Add(shader, translucent);
            }

            hasGlass |= translucent is { IsCompletedSuccessfully: true, Result: { Ok: true, Value: not 0 } };
        }

        return hasGlass;
    }

    /// <summary>
    /// The masks and the denoiser's targets asked for at the traced size, this frame's and last frame's mask and
    /// moments by <paramref name="frameIndex"/>, the mask and tint shading reads at the view's size, and the
    /// denoiser's constants uploaded.
    /// </summary>
    private Result<ShadowBuffers> Prepare(RenderGraph graph, in GpuView view, uint frameIndex)
    {
        float scale = Settings.ShadowScale;
        int width = Scale(view.Width, scale);
        int height = Scale(view.Height, scale);
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        long maskBytes = (long)width * height * BytesPerPixel;
        long groups =
            (long)((width + DenoiseGroupSize - 1) / DenoiseGroupSize)
            * ((height + DenoiseGroupSize - 1) / DenoiseGroupSize);
        int written = (int)(frameIndex % 2);
        Result<GpuBuffer> constants = graph.Buffer(
            GpuDescriptions.GetBuffer("Shadow denoiser constants", Unsafe.SizeOf<ShadowDenoiseConstants>())
        );
        Result<GpuBuffer> tileStates = graph.Buffer(
            GpuDescriptions.GetBuffer("Shadow tile states", groups * sizeof(uint))
        );
        Result<GpuBuffer> sunTiles = graph.Buffer(GpuDescriptions.GetBuffer("Sun shadow tiles", tiles * sizeof(uint)));
        Result<GpuBuffer> tracedMask = graph.Buffer(GpuDescriptions.GetBuffer(_maskNames[written], maskBytes));
        Result<GpuBuffer> previousMask = graph.Buffer(GpuDescriptions.GetBuffer(_maskNames[1 - written], maskBytes));
        // Without glass the sun's light stays white, so no tint is asked for and the graph frees the last one's.
        bool hasGlass = HasGlass(view);
        Result<GpuTexture> tracedTint = hasGlass
            ? Traced("Traced sun tint", GpuFormat.R16G16B16A16Sfloat)
            : Result.Success(GpuTexture.None);
        Result<GpuTexture> sunBlend = Traced("Sun shadow blend", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunHistory = Traced("Sun shadow history", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> moments = Traced(_momentNames[written], GpuFormat.R16G16Sfloat);
        Result<GpuTexture> previousMoments = Traced(_momentNames[1 - written], GpuFormat.R16G16Sfloat);
        // Traced at the picture's size, the traced mask and tint are what shading reads.
        bool isPictureSize = (width, height) == (view.Width, view.Height);
        Result<GpuBuffer> mask = isPictureSize
            ? tracedMask
            : graph.Buffer(GpuDescriptions.GetBuffer("Shadow mask", (long)view.Width * view.Height * BytesPerPixel));
        Result<GpuTexture> sunTint =
            isPictureSize || !hasGlass
                ? tracedTint
                : graph.Texture(
                    GpuDescriptions.GetTexture("Sun tint", view.Width, view.Height, GpuFormat.R16G16B16A16Sfloat)
                );
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [
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
                    sunTint,
                ]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        // Last frame's mask and moments are this frame's history, at the same size only.
        bool hasHistory = view.HasHistory && _traced == (width, height);
        _traced = (width, height);
        ShadowBuffers shadows = new(
            constants.Value,
            tileStates.Value,
            sunTiles.Value,
            tracedTint.Value,
            tracedMask.Value,
            previousMask.Value,
            sunBlend.Value,
            sunHistory.Value,
            moments.Value,
            previousMoments.Value,
            mask.Value,
            sunTint.Value
        );
        ShadowDenoiseConstants denoise = new()
        {
            TileStates = shadows.TileStates.Id,
            SunBlend = shadows.SunBlend.Id,
            SunHistory = shadows.SunHistory.Id,
            Moments = shadows.Moments.Id,
            PreviousMoments = shadows.PreviousMoments.Id,
            Mask = shadows.TracedMask.Id,
            PreviousMask = shadows.PreviousMask.Id,
            SunTint = shadows.TracedTint.Id,
            HasHistory = hasHistory ? 1u : 0u,
            Width = (uint)width,
            Height = (uint)height,
        };
        graph.Upload(shadows.Constants, [denoise]);

        return Result.Success(shadows);

        // A texture at the traced size.
        Result<GpuTexture> Traced(string name, GpuFormat format) =>
            graph.Texture(GpuDescriptions.GetTexture(name, width, height, format));
    }

    /// <summary>
    /// Each traced pixel's shadows, from the view's surfaces, traced, denoised and brought to the picture's size.
    /// </summary>
    private void Record(RenderGraph graph, in TracedInput input, in ShadowBuffers shadows)
    {
        GpuView view = input.View;
        GpuConstants header = new(view.Frame.Id, shadows.Constants.Id);
        (int width, int height) = _traced;
        graph
            .Compute("Trace shadows", _traceShader, header, RenderStage.Light)
            .Reads(
                view.Frame,
                shadows.Constants,
                view.LightTable,
                view.LightTiles,
                view.Instances,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                view.Meshes,
                input.Traced.HitMaterials
            )
            .Writes(shadows.TracedMask, shadows.SunTiles, shadows.TracedTint)
            .DispatchOver(width, height);

        // Denoise: classify the sun's tiles, filter once or twice, as the settings say, then once more as the mask blends
        // with last frame's.
        graph
            .Compute("Classify shadows", _classifyShader, header, RenderStage.Light)
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
            .DispatchOver(width, height);

        // Each pass reads the last one's output; the first pass's is what the next frame blends with.
        int passes = Math.Clamp(Settings.ShadowFilterPasses, 1, _filterPassNames.Length);
        GpuTexture filtered = passes == 1 ? shadows.SunHistory : shadows.SunBlend;
        for (int pass = 0; pass < passes; pass++)
        {
            (GpuTexture source, GpuTexture output) =
                pass == 0 ? (shadows.SunBlend, shadows.SunHistory) : (shadows.SunHistory, shadows.SunBlend);
            graph
                .Compute(_filterPassNames[pass], _filterShader, header with { W = (uint)pass }, RenderStage.Light)
                .Reads(view.Frame, shadows.Constants, shadows.TileStates, view.Depth, view.GBufferNormal, source)
                .Writes(output)
                .DispatchOver(width, height);
        }

        // The sun's slot filtered a last time from the last pass's into the mask the rays wrote, and the tile lights'
        // slots blended with last frame's.
        graph
            .Compute("Blend shadows", _blendShader, header with { W = (uint)passes }, RenderStage.Light)
            .Reads(
                view.Frame,
                shadows.Constants,
                shadows.TileStates,
                view.Depth,
                view.GBufferNormal,
                view.Velocity,
                filtered,
                shadows.PreviousMask,
                shadows.PreviousMoments
            )
            .Writes(shadows.TracedMask)
            .DispatchOver(width, height);
        if (shadows.Mask == shadows.TracedMask)
            return;

        // The mask and the sun's tint at the picture's size, each pixel read from the traced pixels on its surface.
        graph
            .Compute("Upsample shadows", _upsampleShader, header, RenderStage.Light)
            .Reads(
                view.Frame,
                shadows.Constants,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                shadows.TracedMask,
                shadows.TracedTint
            )
            .Writes(shadows.Mask, shadows.SunTint)
            .DispatchOver(view.Width, view.Height);
    }
}

/// <summary>
/// The heap ids of the shadow denoiser's targets and the traced size, the twin of <c>DenoiseShadows.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowDenoiseConstants
{
    public uint TileStates;

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
