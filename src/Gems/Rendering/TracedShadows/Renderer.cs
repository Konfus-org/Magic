using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.TracedShadows;

/// <summary>
/// Traces how much of each light reaches each pixel, denoises it, and makes the frame's <see cref="ShadowMask"/>.
/// </summary>
/// <remarks>
/// It traces through the world as rays see it, at the size that its settings scale the picture to. Each frame it writes
/// the ids of the mask and the sun's tint into the frame's buffer.
/// </remarks>
public sealed class Renderer : IRender<TracedInput, ShadowMask>, IRender, IHookIntoDomainLoading
{
    private const int BytesPerPixel = 16;

    private const int SunTileWidth = 8;

    private const int SunTileHeight = 4;

    /// <summary>
    /// The twin of <c>DenoiseShadows.slang</c>'s group size, a tile state each group.
    /// </summary>
    private const int DenoiseGroupSize = 8;

    /// <summary>
    /// The twins of <c>Scene/Lights.slang</c>'s tile size and words a tile.
    /// </summary>
    private const int LightTileSize = 32;

    private const int LightBuckets = 32;

    /// <summary>
    /// The frames a view's history blends before its shadows are settled: the tile lights' slots take at least 0.12 of
    /// each frame's rays (<c>DenoiseShadows.slang</c>), so after 18 the first frames of a world weigh less than a
    /// tenth; the sun's count is full after 8.
    /// </summary>
    private const int SettleFrames = 18; // PROTO

    /// <summary>
    /// The side of the blue-noise tile, the twin of <c>Core/Random.slang</c>'s <c>BlueNoiseSize</c>.
    /// </summary>
    private const int BlueNoiseSize = 64;

    /// <summary>
    /// The blue noise that the rays pick their points on the lights with, made once.
    /// </summary>
    private static readonly float[] _blueNoise = MakeBlueNoise(); // PROTO

    private static readonly string[] _filterPassNames = ["Filter shadows 1", "Filter shadows 2"];

    /// <summary>
    /// The two filtered masks, blends and moments, the even frames writing the first of each and the odd the second.
    /// </summary>
    private static readonly string[] _maskNames = ["Filtered shadow mask 1", "Filtered shadow mask 2"];

    private static readonly string[] _blendNames = ["Sun shadow blend 1", "Sun shadow blend 2"];

    private static readonly string[] _momentNames = ["Sun shadow moments 1", "Sun shadow moments 2"];

    /// <summary>
    /// Done once the shaders are built or failed, and at once while shadows are off or no ray is traced.
    /// </summary>
    /// <remarks>
    /// A domain loading waits for it, so a shader that fails is told and logged before the world shows.
    /// </remarks>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TracedShadowsSettings _settings;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _traceShader;

    private readonly ShaderEntry _classifyShader;

    private readonly ShaderEntry _filterShader;

    private readonly ShaderEntry _lastFilterShader;

    private readonly ShaderEntry _tileFilterShader;

    private readonly ShaderEntry _keepTilesShader;

    private readonly ShaderEntry _upsampleShader;

    /// <summary>
    /// The size the frame drawn last traced each view at, which the next blends with only at the same size, and the
    /// frames its history blended.
    /// </summary>
    private readonly List<(int Width, int Height, int Blended)> _traced = [];

    /// <summary>
    /// The wait of the world that arrived for the shadows to settle, which the frame that blends enough frames or
    /// traces none completes. Render thread.
    /// </summary>
    private readonly SettleWait _settling;

    internal Renderer(TracedShadowsSettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        _settling = new SettleWait(threads);
        _settings = settings;
        _gpu = gpu;
        _traceShader = PassShaders.GetEntry(assets, "TraceShadows", "TraceShadows");
        _classifyShader = PassShaders.GetEntry(assets, "DenoiseShadows", "ClassifyShadows");
        _filterShader = PassShaders.GetEntry(assets, "DenoiseShadows", "FilterShadows");
        _lastFilterShader = PassShaders.GetEntry(assets, "DenoiseShadows", "FilterLastAndBlendShadows");
        _tileFilterShader = PassShaders.GetEntry(assets, "DenoiseShadows", "FilterTileShadows");
        _keepTilesShader = PassShaders.GetEntry(assets, "DenoiseShadows", "KeepLightTiles");
        _upsampleShader = PassShaders.GetEntry(assets, "DenoiseShadows", "UpsampleShadows");
    }

    /// <summary>
    /// Traces and denoises the frame's shadows, and writes their ids into the frame's buffer.
    /// </summary>
    /// <returns>
    /// The frame's shadows, or none while they are off, the passes' shaders build, or what they write is not made.
    /// </returns>
    public ShadowMask? Render(in RenderFrame frame, RenderGraph graph, in TracedInput input)
    {
        // Off, each view's history starts afresh once it is back on.
        if (!_settings.IsTracedShadowsOn)
        {
            _traced.Clear();

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each.
        GpuState state = _gpu.GetState(
            _traceShader,
            _classifyShader,
            _filterShader,
            _lastFilterShader,
            _tileFilterShader,
            _keepTilesShader,
            _upsampleShader
        );
        if (state == GpuState.Failed)
        {
            _settling.End();
            frame.Fail(RenderFailure.Shader, "Shadows are not traced", "a shader of the shadows was not built.");
        }

        if (state != GpuState.Ready)
            return null;

        // The masks and the denoiser's targets at the traced size, this frame's and last frame's filtered mask and
        // moments by the frame's index, a copy of the view's tiles of lights for the next frame, and the mask and tint
        // shading reads at the view's size. Without local lights, the tile lights' slots have nothing to filter, so the
        // traced mask is the filtered one.
        GpuView view = input.View;
        (int width, int height) = view.Scaled(_settings.ShadowScale);
        int viewIndex = frame.View;
        long tiles = (long)((width + SunTileWidth - 1) / SunTileWidth) * ((height + SunTileHeight - 1) / SunTileHeight);
        long maskBytes = (long)width * height * BytesPerPixel;
        long groups =
            (long)((width + DenoiseGroupSize - 1) / DenoiseGroupSize)
            * ((height + DenoiseGroupSize - 1) / DenoiseGroupSize);
        long lightTileWords =
            (long)((view.Width + LightTileSize - 1) / LightTileSize)
            * ((view.Height + LightTileSize - 1) / LightTileSize)
            * LightBuckets;
        int written = (int)(frame.Index % 2);
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Shadow denoiser constants", Unsafe.SizeOf<ShadowDenoiseConstants>()),
            frame.View
        );
        Result<GpuBuffer> tileStates = _gpu.GetBuffer(new("Shadow tile states", groups * sizeof(uint)), frame.View);
        Result<GpuBuffer> blueNoise = _gpu.GetBuffer(new("Blue noise", _blueNoise.Length * sizeof(float)), frame.View);
        Result<GpuBuffer> sunTiles = _gpu.GetBuffer(new("Sun shadow tiles", tiles * sizeof(uint)), frame.View);
        Result<GpuBuffer> tracedMask = _gpu.GetBuffer(new("Traced shadow mask", maskBytes), frame.View);
        bool hasTileLights = view.LightCount > 0;
        Result<GpuBuffer> filteredMask = hasTileLights
            ? _gpu.GetBuffer(new(_maskNames[written], maskBytes), frame.View)
            : tracedMask;
        Result<GpuBuffer> previousMask = _gpu.GetBuffer(new(_maskNames[1 - written], maskBytes), frame.View);
        Result<GpuBuffer> lastLightTiles = _gpu.GetBuffer(
            new("Last light tiles", lightTileWords * sizeof(uint)),
            frame.View
        );
        // Without glass, which the view has targets for only while a material draws translucent, the sun's light stays
        // white, so no tint is asked for and the graph frees the last one's.
        bool hasGlass = !view.GlassDepth.IsNone;
        Result<GpuTexture> tracedTint = hasGlass
            ? GetTracedTexture("Traced sun tint", GpuFormat.R16G16B16A16Sfloat)
            : Result.Success(GpuTexture.None);
        Result<GpuTexture> sunOccluder = GetTracedTexture("Sun occluder distance", GpuFormat.R32Sfloat);
        Result<GpuTexture> sunBlend = GetTracedTexture(_blendNames[written], GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunHistory = GetTracedTexture(_blendNames[1 - written], GpuFormat.R16G16Sfloat);
        Result<GpuTexture> sunFiltered = GetTracedTexture("Sun shadow filtered", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> moments = GetTracedTexture(_momentNames[written], GpuFormat.R16G16Sfloat);
        Result<GpuTexture> previousMoments = GetTracedTexture(_momentNames[1 - written], GpuFormat.R16G16Sfloat);
        // Traced at the picture's size, the filtered mask and the traced tint are what shading reads.
        bool isPictureSize = (width, height) == (view.Width, view.Height);
        Result<GpuBuffer> mask = isPictureSize
            ? filteredMask
            : _gpu.GetBuffer(new("Shadow mask", (long)view.Width * view.Height * BytesPerPixel), frame.View);
        Result<GpuTexture> sunTint =
            isPictureSize || !hasGlass
                ? tracedTint
                : _gpu.GetTexture(new("Sun tint", view.Width, view.Height, GpuFormat.R16G16B16A16Sfloat), frame.View);
        Result asked = Result.FindFailure(
            constants,
            tileStates,
            blueNoise,
            sunTiles,
            tracedMask,
            filteredMask,
            previousMask,
            lastLightTiles,
            tracedTint,
            sunOccluder,
            sunBlend,
            sunHistory,
            sunFiltered,
            moments,
            previousMoments,
            mask,
            sunTint
        );
        if (!asked.Ok)
        {
            _settling.End();
            frame.Fail(RenderFailure.Resource, "Shadows are not traced", asked.Error);

            return null;
        }

        // Last frame's mask and moments of this view are this frame's history, at the same size only.
        while (_traced.Count <= frame.View)
        {
            _traced.Add(default);
        }

        (int lastWidth, int lastHeight, int blended) = _traced[frame.View];
        bool hasHistory = view.HasHistory && (lastWidth, lastHeight) == (width, height);
        blended = hasHistory ? blended + 1 : 0;
        _traced[frame.View] = (width, height, blended);
        if (blended >= SettleFrames)
            _settling.End();

        ShadowDenoiseConstants denoise = new()
        {
            TileStates = tileStates.Value.Id,
            SunTiles = sunTiles.Value.Id,
            SunOccluder = sunOccluder.Value.Id,
            SunBlend = sunBlend.Value.Id,
            SunHistory = sunHistory.Value.Id,
            SunFiltered = sunFiltered.Value.Id,
            Moments = moments.Value.Id,
            PreviousMoments = previousMoments.Value.Id,
            Mask = tracedMask.Value.Id,
            FilteredMask = filteredMask.Value.Id,
            PreviousMask = previousMask.Value.Id,
            LastLightTiles = lastLightTiles.Value.Id,
            SunTint = tracedTint.Value.Id,
            HasHistory = hasHistory ? 1u : 0u,
            Width = (uint)width,
            Height = (uint)height,
            BlueNoise = blueNoise.Value.Id,
        };
        // Not staged: nothing is recorded, and the frame's row stays the view's default, which the shaders read as none.
        GpuShadows row = new() { Mask = mask.Value.Id, SunTint = sunTint.Value.Id };
        if (
            !graph.Upload(constants.Value, [denoise])
            || !graph.Upload(view.Frame, [row], GpuShadows.Offset)
            || !graph.Upload(blueNoise.Value, _blueNoise)
        )
            return null;

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
                input.Traced.HitMaterials,
                tileStates.Value,
                blueNoise.Value
            )
            .Writes(tracedMask.Value, sunTiles.Value, sunOccluder.Value, tracedTint.Value)
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
                tracedMask.Value,
                sunHistory.Value,
                previousMoments.Value
            )
            .Writes(tileStates.Value, sunBlend.Value, moments.Value)
            .DispatchOver(width, height);

        // Each pass reads the last one's output. The second writes over last frame's blend, which is read by then.
        int passes = Math.Clamp(_settings.ShadowFilterPasses, 1, _filterPassNames.Length);
        GpuTexture filtered = passes == 1 ? sunFiltered.Value : sunHistory.Value;
        for (int pass = 0; pass < passes; pass++)
        {
            (GpuTexture source, GpuTexture output) =
                pass == 0 ? (sunBlend.Value, sunFiltered.Value) : (sunFiltered.Value, sunHistory.Value);
            graph
                .Compute(_filterPassNames[pass], _filterShader, header with { W = (uint)pass }, RenderStages.Light)
                .Reads(
                    view.Frame,
                    constants.Value,
                    tileStates.Value,
                    view.Depth,
                    view.GBufferNormal,
                    sunOccluder.Value,
                    source
                )
                .Writes(output)
                .DispatchOver(width, height);
        }

        // The sun's slot filtered a last time from the last pass's into the mask the rays wrote, and the tile lights'
        // slots blended with last frame's slots of the same lights.
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
                view.LightTiles,
                sunOccluder.Value,
                filtered,
                previousMask.Value,
                previousMoments.Value,
                lastLightTiles.Value
            )
            .Writes(tracedMask.Value)
            .DispatchOver(width, height);

        // The tile lights' slots filtered over their tiles into the mask the next frame blends with, and the view's
        // tiles of lights kept for it. A frame that follows one without lights finds none in them, so it never reads the
        // filtered mask that frame did not write.
        if (hasTileLights)
        {
            graph
                .Compute("Filter tile shadows", _tileFilterShader, header, RenderStages.Light)
                .Reads(view.Frame, constants.Value, view.LightTiles, view.Depth, view.GBufferNormal, tracedMask.Value)
                .Writes(filteredMask.Value)
                .DispatchOver(width, height);
        }

        graph
            .Compute("Keep light tiles", _keepTilesShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, view.LightTiles)
            .Writes(lastLightTiles.Value)
            .DispatchOver((int)lightTileWords, 1);

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
                    filteredMask.Value,
                    tracedTint.Value
                )
                .Writes(mask.Value, sunTint.Value)
                .DispatchOver(view.Width, view.Height);
        }

        return new ShadowMask(mask.Value, sunTint.Value, GpuTexture.None, GpuTexture.None);

        Result<GpuTexture> GetTracedTexture(string name, GpuFormat format) =>
            _gpu.GetTexture(new(name, width, height, format), viewIndex);
    }

    /// <summary>
    /// Asks for every shader of the shadows each frame while they are on and rays are traced, so each is built or
    /// failed before a domain shows. Ends the wait for the shadows to settle while they cannot trace.
    /// </summary>
    public void Render(in RenderFrame frame, RenderGraph graph)
    {
        if (
            !_settings.IsTracedShadowsOn
            || !_gpu.Features.HasRayQuery
            || !_gpu.IsAnyLoading(
                _traceShader,
                _classifyShader,
                _filterShader,
                _lastFilterShader,
                _tileFilterShader,
                _keepTilesShader,
                _upsampleShader
            )
        )
            _built.TrySetResult();

        // A maker that is out leaves the shadows without the view or the traced world for good: the failure is already
        // told.
        if (
            !_settings.IsTracedShadowsOn
            || !_gpu.Features.HasRayQuery
            || frame.IsUnavailable<GpuView>()
            || frame.IsUnavailable<TracedWorld>()
        )
            _settling.End();
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    /// <summary>
    /// Returns once the shadows of a view blended enough frames of the world that arrived, or at once while it traces
    /// none.
    /// </summary>
    public Task OnDomainArrivedAsync(CancellationToken cancel) => _settling.WaitAsync(cancel);

    /// <summary>
    /// A tile of blue noise, by void and cluster (Ulichney, 1993): each pixel's rank among the tile's, from 0 to 1. A
    /// tenth of the pixels is first spread evenly, by moving the most crowded to the emptiest place until none moves;
    /// those are ranked by taking the most crowded away, the rest by filling the emptiest place in turn.
    /// </summary>
    private static float[] MakeBlueNoise()
    {
        const int Pixels = BlueNoiseSize * BlueNoiseSize;
        const float Sigma = 1.5f;

        // How much a set pixel crowds each other, by their distance across the wrapping tile.
        float[] kernel = new float[Pixels];
        for (int i = 0; i < Pixels; i++)
        {
            int x = Math.Min(i % BlueNoiseSize, BlueNoiseSize - (i % BlueNoiseSize));
            int y = Math.Min(i / BlueNoiseSize, BlueNoiseSize - (i / BlueNoiseSize));
            kernel[i] = MathF.Exp(-((x * x) + (y * y)) / (2 * Sigma * Sigma));
        }

        bool[] isSet = new bool[Pixels];
        float[] crowding = new float[Pixels];
        uint seed = 7;
        int count = 0;
        while (count < Pixels / 10)
        {
            seed = unchecked((seed * 1664525) + 1013904223);
            int at = (int)((seed >> 8) % Pixels);
            if (isSet[at])
                continue;

            Set(at, true);
            count++;
        }

        // Until the emptiest place is the one the most crowded pixel just left.
        int left;
        int filled;
        do
        {
            left = Find(true);
            Set(left, false);
            filled = Find(false);
            Set(filled, true);
        } while (filled != left);

        float[] ranks = new float[Pixels];
        bool[] start = (bool[])isSet.Clone();
        float[] startCrowding = (float[])crowding.Clone();
        for (int rank = count - 1; rank >= 0; rank--)
        {
            int crowded = Find(true);
            Set(crowded, false);
            ranks[crowded] = rank;
        }

        isSet = start;
        crowding = startCrowding;
        for (int rank = count; rank < Pixels; rank++)
        {
            int empty = Find(false);
            Set(empty, true);
            ranks[empty] = rank;
        }

        for (int i = 0; i < Pixels; i++)
        {
            ranks[i] = (ranks[i] + 0.5f) / Pixels;
        }

        return ranks;

        // The most crowded set pixel, or the least crowded empty one.
        int Find(bool set)
        {
            int found = -1;
            for (int i = 0; i < Pixels; i++)
            {
                bool isBetter = found < 0 || (set ? crowding[i] > crowding[found] : crowding[i] < crowding[found]);
                if (isSet[i] == set && isBetter)
                    found = i;
            }

            return found;
        }

        void Set(int at, bool value)
        {
            isSet[at] = value;
            float sign = value ? 1 : -1;
            int ax = at % BlueNoiseSize;
            int ay = at / BlueNoiseSize;
            for (int i = 0; i < Pixels; i++)
            {
                int dx = ((i % BlueNoiseSize) - ax + BlueNoiseSize) % BlueNoiseSize;
                int dy = ((i / BlueNoiseSize) - ay + BlueNoiseSize) % BlueNoiseSize;
                crowding[i] += sign * kernel[(dy * BlueNoiseSize) + dx];
            }
        }
    }
}

/// <summary>
/// The heap ids of the shadow denoiser's targets and the traced size, the twin of <c>DenoiseShadows.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowDenoiseConstants
{
    internal uint TileStates;

    internal uint SunTiles;

    internal uint SunOccluder;

    internal uint SunBlend;

    internal uint SunHistory;

    internal uint SunFiltered;

    internal uint Moments;

    internal uint PreviousMoments;

    /// <summary>
    /// The traced mask, the rays' and then the blended, the filtered mask, this frame's and last frame's, last frame's
    /// tiles of lights, and the sun's tint as traced.
    /// </summary>
    internal uint Mask;

    internal uint FilteredMask;

    internal uint PreviousMask;

    internal uint LastLightTiles;

    internal uint SunTint;

    /// <summary>
    /// 0 on the first frame at a size, when last frame's targets hold nothing to blend with.
    /// </summary>
    internal uint HasHistory;

    /// <summary>
    /// The traced size.
    /// </summary>
    internal uint Width;

    internal uint Height;

    internal uint BlueNoise;
}
