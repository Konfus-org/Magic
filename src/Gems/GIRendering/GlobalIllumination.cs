using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// What lights the world indirectly: the lit cards, a pool of slots each holding the irradiance on an instance's
/// cards, given to the instances that matter most to the view, with the radiosity each tile of a card receives
/// traced beside them; the world probes, a clipmap of spheres of radiance around the camera that trace the field
/// and read the cards; the screen probes, one a tile of pixels, that trace the screen and the field and end in the
/// world probes; the short-range occlusion of every pixel; the sharp reflections, a ray every stride of pixels; and
/// the passes that keep each up to date, a budget of each a frame.
/// </summary>
internal sealed class GlobalIllumination(IGraphicsApi gpu, GpuPipelines pipelines) : IDisposable
{
    public const int ProbeLevels = 4;

    public const int ProbesPerAxis = 16;

    public const int ProbesPerLevel = ProbesPerAxis * ProbesPerAxis * ProbesPerAxis;

    public const int ProbeBudget = 256;

    private const int LitPoolTexels = 2048;

    private const int LitCardTexels = GpuCard.MaxTexels / 2;

    private const int LitSlotWidth = LitCardTexels * 3;

    private const int LitSlotHeight = LitCardTexels * 2;

    private const int LitSlots = LitPoolTexels / LitSlotWidth * (LitPoolTexels / LitSlotHeight);

    private const int LitTileTexels = 8;

    private const int LitRadiosityTexels = LitPoolTexels / LitTileTexels;

    private const int LitLightsPerSlot = 64;

    private const int MaxSlotsPerFrame = 8;

    private const int ProbeStateBytes = 16;

    private const int ScreenProbeBytes = 48;

    private const int AssignPeriod = 16;

    private const int ProbeTexels = 16;

    private const int ProbeIrradianceTexels = 8;

    private const int ProbesPerAtlasRow = 64;

    private const int ScreenTile = 8;

    private const int ScreenTexels = 8;

    private const int Threads = 64;

    private const float FinestSpacing = 1;

    private const float RayRange = 64;

    private readonly GpuPass _cardLightsPass = pipelines.Compute("GI: list card lights", "LitCards", "LitCardLights");

    private readonly GpuPass _cardRadiosityPass = pipelines.Compute(
        "GI: card radiosity",
        "LitCards",
        "LitCardRadiosity",
        PassShaders.Atmosphere | PassShaders.Tracer
    );

    private readonly GpuPass _lightCardsPass = pipelines.Compute(
        "GI: light cards",
        "LitCards",
        "LitCardShade",
        PassShaders.Atmosphere
    );

    private readonly GpuPass _scrollProbesPass = pipelines.Compute(
        "GI: scroll probes",
        "WorldProbes",
        "WorldProbeScroll"
    );

    private readonly GpuPass _selectProbesPass = pipelines.Compute(
        "GI: select probes",
        "WorldProbes",
        "WorldProbeSelect"
    );

    private readonly GpuPass _traceProbesPass = pipelines.Compute(
        "GI: trace probes",
        "WorldProbes",
        "WorldProbeTrace",
        PassShaders.Atmosphere | PassShaders.Tracer
    );

    private readonly GpuPass _placeScreenProbesPass = pipelines.Compute(
        "GI: place screen probes",
        "ScreenProbes",
        "ScreenProbePlace"
    );

    private readonly GpuPass _traceScreenProbesPass = pipelines.Compute(
        "GI: trace screen probes",
        "ScreenProbes",
        "ScreenProbeTrace",
        PassShaders.Atmosphere | PassShaders.Tracer
    );

    private readonly GpuPass _reprojectScreenProbesPass = pipelines.Compute(
        "GI: reproject screen probes",
        "ScreenProbes",
        "ScreenProbeTemporal"
    );

    private readonly GpuPass _occlusionPass = pipelines.Compute("GI: occlusion", "Occlusion", "Occlusion");

    private readonly GpuPass _blurOcclusionPass = pipelines.Compute("GI: blur occlusion", "Occlusion", "OcclusionBlur");

    private readonly GpuPass _filterScreenProbesPass = pipelines.Compute(
        "GI: filter screen probes",
        "ScreenProbes",
        "ScreenProbeFilter"
    );

    private readonly GpuPass _traceReflectionsPass = pipelines.Compute(
        "Reflections: trace",
        "Reflections",
        "ReflectionTrace",
        PassShaders.Atmosphere | PassShaders.Tracer
    );

    private readonly GpuPass _resolveReflectionsPass = pipelines.Compute(
        "Reflections: resolve",
        "Reflections",
        "ReflectionResolve"
    );

    private readonly GpuPass _reprojectReflectionsPass = pipelines.Compute(
        "Reflections: reproject",
        "Reflections",
        "ReflectionTemporal"
    );

    private readonly uint[] _slotOfInstance = new uint[InstanceTable.Capacity];

    private readonly GpuLitSlot[] _slots = new GpuLitSlot[LitSlots];

    private readonly bool[] _isSlotUsed = new bool[LitSlots];

    private readonly bool[] _isWanted = new bool[InstanceTable.Capacity];

    private readonly Stack<int> _freeSlots = new();

    private readonly Queue<uint> _pendingSlots = new();

    private readonly List<(float Importance, int Index)> _candidates = [];

    private readonly uint[] _batch = new uint[MaxSlotsPerFrame];

    private int _slotsPerFrame = MaxSlotsPerFrame;

    private bool _hasReflections = true;

    private int _reflectionStride = 1;

    private readonly GpuTexture[] _screenFiltered = new GpuTexture[2];

    private readonly GpuTexture[] _reflections = new GpuTexture[2];

    private GpuTexture _reflectionTraced;

    private GpuTexture _reflectionResolved;

    private GpuTexture _occlusionRaw;

    private GpuTexture _occlusion;

    private readonly GpuBuffer[] _screenProbes = new GpuBuffer[2];

    private GpuTexture _litPool;

    private GpuTexture _radiosity;

    private GpuTexture _probeRadiance;

    private GpuTexture _probeDistance;

    private GpuTexture _probeIrradiance;

    private GpuTexture _screenRadiance;

    private GpuBuffer _litSlots;

    private GpuBuffer _slotInstances;

    private GpuBuffer _slotBatch;

    private GpuBuffer _slotLights;

    private GpuBuffer _probeStates;

    private GpuBuffer _probeList;

    private GpuBuffer _screenIrradiance;

    private int _screenProbesX;

    private int _screenProbesY;

    private int _current;

    private int _batchCount;

    private int _cursor;

    private uint _framesSinceAssigned = AssignPeriod;

    private bool _areSlotsDirty = true;

    private bool _isTableReady;

    public GpuBuffer Constants { get; private set; }

    /// <summary>
    /// Whether the screen tables must be made again before the next frame, as the reflections' stride changed.
    /// </summary>
    public bool NeedsResize { get; private set; }

    public void Dispose()
    {
        Destroy(null);
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [Constants, _litSlots, _slotInstances, _slotBatch, _slotLights, _probeStates, _probeList]
        )
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [_litPool, _radiosity, _probeRadiance, _probeDistance, _probeIrradiance]
        )
        {
            if (!texture.IsNone)
                gpu.Destroy(texture);
        }
    }

    public Result Create()
    {
        for (int slot = LitSlots - 1; slot >= 0; slot--)
        {
            _freeSlots.Push(slot);
        }

        (string Name, long Bytes)[] descriptions =
        [
            ("GI constants", GiConstants.Bytes),
            ("Lit slots of instances", (long)InstanceTable.Capacity * 4),
            ("Lit slot instances", (long)LitSlots * GpuLitSlot.Bytes),
            ("Lit slot batch", (long)MaxSlotsPerFrame * 4),
            ("Lit slot lights", (long)LitSlots * (LitLightsPerSlot + 1) * 4),
            ("Probe states", (long)ProbeLevels * ProbesPerLevel * ProbeStateBytes),
            ("Probe list", (long)(1 + ProbeBudget) * 4),
        ];
        GpuBuffer[] made = new GpuBuffer[descriptions.Length];
        for (int i = 0; i < descriptions.Length; i++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = descriptions[i].Bytes,
                    Usage = BufferUsages.Storage,
                    Name = descriptions[i].Name,
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"the {descriptions[i].Name} buffer was not made: {buffer.Error}");

            made[i] = buffer.Value;
        }

        (Constants, _litSlots, _slotInstances, _slotBatch, _slotLights, _probeStates, _probeList) = (
            made[0],
            made[1],
            made[2],
            made[3],
            made[4],
            made[5],
            made[6]
        );
        int probeRows = ProbeLevels * ProbesPerLevel / ProbesPerAtlasRow;
        Result<GpuTexture> litPool = Make("Lit card pool", LitPoolTexels, LitPoolTexels, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> radiosity = Make(
            "Card radiosity",
            LitRadiosityTexels,
            LitRadiosityTexels,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> radiance = Make(
            "Probe radiance",
            ProbesPerAtlasRow * ProbeTexels,
            probeRows * ProbeTexels,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> distance = Make(
            "Probe distances",
            ProbesPerAtlasRow * ProbeTexels,
            probeRows * ProbeTexels,
            GpuFormat.R16G16Sfloat
        );
        Result<GpuTexture> irradiance = Make(
            "Probe irradiance",
            ProbesPerAtlasRow * ProbeIrradianceTexels,
            probeRows * ProbeIrradianceTexels,
            GpuFormat.R16G16B16A16Sfloat
        );
        foreach (
            Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)
                [litPool, radiosity, radiance, distance, irradiance]
        )
        {
            if (!texture.Ok)
                return Result.Failure($"a global illumination table was not made: {texture.Error}");
        }

        (_litPool, _radiosity, _probeRadiance, _probeDistance, _probeIrradiance) = (
            litPool.Value,
            radiosity.Value,
            radiance.Value,
            distance.Value,
            irradiance.Value
        );

        return Result.Success();
    }

    /// <summary>
    /// How much a frame does: the cards lit a frame, whether the sharp reflections are traced at all, and the pixels
    /// across each reflection ray stands for, 1 or 2.
    /// </summary>
    public void Configure(int slotsPerFrame, bool hasReflections, int reflectionStride)
    {
        _slotsPerFrame = Math.Clamp(slotsPerFrame, 1, MaxSlotsPerFrame);
        _hasReflections = hasReflections;
        int stride = Math.Clamp(reflectionStride, 1, 2);
        NeedsResize |= stride != _reflectionStride;
        _reflectionStride = stride;
    }

    /// <summary>
    /// Makes the screen probes for an output size, the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height, RenderGraph graph)
    {
        Destroy(graph);
        NeedsResize = false;
        _screenProbesX = (width + ScreenTile - 1) / ScreenTile;
        _screenProbesY = (height + ScreenTile - 1) / ScreenTile;
        int count = _screenProbesX * _screenProbesY;
        Result<GpuTexture> traced = Make(
            "Screen probe radiance",
            _screenProbesX * ScreenTexels,
            _screenProbesY * ScreenTexels,
            GpuFormat.R16G16B16A16Sfloat
        );
        if (!traced.Ok)
            return Result.Failure($"the screen probe atlas was not made: {traced.Error}");

        _screenRadiance = traced.Value;
        for (int i = 0; i < 2; i++)
        {
            Result<GpuTexture> filtered = Make(
                $"Screen probe radiance filtered {i}",
                _screenProbesX * ScreenTexels,
                _screenProbesY * ScreenTexels,
                GpuFormat.R16G16B16A16Sfloat
            );
            if (!filtered.Ok)
                return Result.Failure($"a screen probe atlas was not made: {filtered.Error}");

            _screenFiltered[i] = filtered.Value;
            Result<GpuBuffer> probes = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = (long)count * ScreenProbeBytes,
                    Usage = BufferUsages.Storage,
                    Name = $"Screen probes {i}",
                }
            );
            if (!probes.Ok)
                return Result.Failure($"a screen probe table was not made: {probes.Error}");

            _screenProbes[i] = probes.Value;
        }

        Result<GpuBuffer> irradiance = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)count * 16,
                Usage = BufferUsages.Storage,
                Name = "Screen probe irradiance",
            }
        );
        if (!irradiance.Ok)
            return Result.Failure($"the screen probe irradiance table was not made: {irradiance.Error}");

        _screenIrradiance = irradiance.Value;
        Result<GpuTexture> reflectionTraced = Make(
            "Reflections traced",
            (width + _reflectionStride - 1) / _reflectionStride,
            (height + _reflectionStride - 1) / _reflectionStride,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> reflectionResolved = Make(
            "Reflections resolved",
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> reflections0 = Make("Reflections 0", width, height, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> reflections1 = Make("Reflections 1", width, height, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> occlusionRaw = Make("Occlusion searched", width, height, GpuFormat.R8Unorm);
        Result<GpuTexture> occlusion = Make("Occlusion", width, height, GpuFormat.R8Unorm);
        foreach (
            Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)
                [reflectionTraced, reflectionResolved, reflections0, reflections1, occlusionRaw, occlusion]
        )
        {
            if (!texture.Ok)
                return Result.Failure($"a screen texture was not made: {texture.Error}");
        }

        (_reflectionTraced, _reflectionResolved, _reflections[0], _reflections[1], _occlusionRaw, _occlusion) = (
            reflectionTraced.Value,
            reflectionResolved.Value,
            reflections0.Value,
            reflections1.Value,
            occlusionRaw.Value,
            occlusion.Value
        );

        return Result.Success();
    }

    /// <summary>
    /// The frame's slots given out, the batch picked and the constants and tables uploaded, in a transfer pass the
    /// caller opened. The instances that matter most to the view hold the slots: the largest on screen first.
    /// </summary>
    public void Prepare(
        RenderGraph graph,
        ReadOnlySpan<GpuInstance> instances,
        Vector3 cameraPosition,
        uint frameIndex,
        Vector3 fallbackIrradiance
    )
    {
        _current = 1 - _current;
        if (!_isTableReady)
        {
            _isTableReady = true;
            foreach (
                GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                    [_probeStates, _probeList, _litSlots, _slotLights, _screenProbes[0], _screenProbes[1]]
            )
            {
                graph.Use(buffer, GpuAccess.CopyDestination);
                graph.Recorder.FillBuffer(buffer, 0);
            }
        }

        if (++_framesSinceAssigned >= AssignPeriod)
        {
            _framesSinceAssigned = 0;
            AssignSlots(instances, cameraPosition);
        }

        if (_areSlotsDirty)
        {
            _areSlotsDirty = false;
            Upload(graph, _litSlots, MemoryMarshal.AsBytes(_slotOfInstance.AsSpan()));
            Upload(graph, _slotInstances, MemoryMarshal.AsBytes(_slots.AsSpan()));
        }

        PickBatch();
        if (_batchCount > 0)
            Upload(graph, _slotBatch, MemoryMarshal.AsBytes(_batch.AsSpan(0, _batchCount)));

        // A slot lit this frame has something to blend with from the next.
        for (int i = 0; i < _batchCount; i++)
        {
            if (_slots[_batch[i]].Frames != 0)
                continue;

            _slots[_batch[i]].Frames = 1;
            _areSlotsDirty = true;
        }

        GiConstants constants = new()
        {
            ProbeSpacings = new Vector4(FinestSpacing, FinestSpacing * 2, FinestSpacing * 4, FinestSpacing * 8),
            FallbackIrradiance = new Vector4(fallbackIrradiance, 0),
            LitPool = _litPool.Id,
            LitSlots = _litSlots.Id,
            SlotInstances = _slotInstances.Id,
            SlotLights = _slotLights.Id,
            ProbeStates = _probeStates.Id,
            ProbeList = _probeList.Id,
            ProbeRadiance = _probeRadiance.Id,
            ProbeDistance = _probeDistance.Id,
            ProbeIrradiance = _probeIrradiance.Id,
            CardRadiosity = _radiosity.Id,
            ScreenRadiance = _screenRadiance.Id,
            ScreenRadiancePrevious = _screenFiltered[1 - _current].Id,
            ScreenProbes = _screenProbes[_current].Id,
            ScreenProbesPrevious = _screenProbes[1 - _current].Id,
            ScreenIrradiance = _screenIrradiance.Id,
            ScreenProbesX = (uint)_screenProbesX,
            ScreenProbesY = (uint)_screenProbesY,
            SlotBatch = _slotBatch.Id,
            ProbeBudget = ProbeBudget,
            FrameIndex = frameIndex,
            RayRange = RayRange,
            ScreenFiltered = _screenFiltered[_current].Id,
            ReflectionTraced = _reflectionTraced.Id,
            ReflectionResolved = _reflectionResolved.Id,
            Reflections = _reflections[_current].Id,
            ReflectionsPrevious = _reflections[1 - _current].Id,
            HasReflections = _hasReflections ? 1u : 0u,
            OcclusionRaw = _occlusionRaw.Id,
            Occlusion = _occlusion.Id,
            ReflectionStride = (uint)_reflectionStride,
        };
        for (int level = 0; level < ProbeLevels; level++)
        {
            float spacing = constants.ProbeSpacings[level];
            Vector3 scaled = cameraPosition / spacing;
            constants.ProbeOrigins[level] = new ProbeOrigin(
                (int)MathF.Floor(scaled.X) - (ProbesPerAxis / 2),
                (int)MathF.Floor(scaled.Y) - (ProbesPerAxis / 2),
                (int)MathF.Floor(scaled.Z) - (ProbesPerAxis / 2),
                0
            );
        }

        GpuUpload upload = gpu.BeginUpload(GiConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, GiConstants.Bytes);
    }

    /// <summary>
    /// The lit cards, the world probes, the screen probes, the occlusion and the reflections brought up to date,
    /// after the GBuffer, the depth pyramid and the sun's pages, before the lighting reads them.
    /// </summary>
    public void Record(
        RenderGraph graph,
        GpuBuffer frameConstants,
        GpuBuffer instances,
        GpuBuffer visibleClusters,
        SunShadows shadows,
        Lighting lighting,
        DistanceField field,
        Translucency translucency,
        CardTable cards,
        FrameTargets targets,
        bool usesRayQuery
    )
    {
        GpuConstants header = new(frameConstants.Id, shadows.Constants.Id, lighting.Constants.Id, field.Constants.Id);
        if (_batchCount > 0)
        {
            uint batchCards = (uint)(_batchCount * GpuCard.PerCapture);
            GpuPassRun cardLightsRun = _cardLightsPass
                .Run(graph, header)
                .Reads(frameConstants, Constants, instances, _slotBatch, _slotInstances)
                .Writes(_slotLights);
            lighting.UseForCards(graph);
            cardLightsRun.Dispatch((uint)_batchCount);

            GpuPassRun cardRadiosityRun = _cardRadiosityPass
                .Run(graph, header, usesRayQuery: usesRayQuery)
                .Reads(frameConstants, Constants, instances, _slotBatch, _slotInstances)
                .Writes(_radiosity);
            UseForCards(graph);
            UseProbes(graph);
            cards.UseForReading(graph);
            field.UseForSampling(graph);
            lighting.UseForCards(graph);
            translucency.UseForSampling(graph);
            cardRadiosityRun.Dispatch(LitCardTexels / LitTileTexels, LitCardTexels / LitTileTexels, batchCards);

            GpuPassRun lightCardsRun = _lightCardsPass
                .Run(graph, header)
                .Reads(frameConstants, Constants, instances, _slotBatch, _slotInstances, _slotLights, _radiosity)
                .Writes(_litPool);
            cards.UseForReading(graph);
            shadows.UseForSampling(graph);
            lighting.UseForCards(graph);
            translucency.UseForSampling(graph);
            lightCardsRun.Dispatch(Groups(LitCardTexels, 8), Groups(LitCardTexels, 8), batchCards);
        }

        int cells = ProbeLevels * ProbesPerLevel;
        _scrollProbesPass
            .Run(graph, header)
            .Reads(Constants, lighting.Constants)
            .Writes(_probeStates, _probeList)
            .Dispatch(Groups(cells));

        GpuPassRun selectProbesRun = _selectProbesPass
            .Run(graph, header)
            .Reads(Constants, lighting.Constants)
            .Writes(_probeStates, _probeList);
        field.UseForChanges(graph);
        selectProbesRun.Dispatch(Groups(cells));

        GpuPassRun traceProbesRun = _traceProbesPass
            .Run(graph, header, usesRayQuery: usesRayQuery)
            .Reads(frameConstants, Constants, instances, _probeList)
            .Writes(_probeStates, _probeRadiance, _probeDistance, _probeIrradiance);
        UseForCards(graph);
        cards.UseForReading(graph);
        field.UseForSampling(graph);
        lighting.UseForCards(graph);
        translucency.UseForSampling(graph);
        traceProbesRun.Dispatch(ProbeBudget);

        int probeCount = _screenProbesX * _screenProbesY;
        _placeScreenProbesPass
            .Run(graph, header)
            .Reads(
                frameConstants,
                Constants,
                lighting.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.Velocity
            )
            .Writes(_screenProbes[_current])
            .Dispatch(Groups(_screenProbesX, 8), Groups(_screenProbesY, 8));

        GpuPassRun traceScreenProbesRun = _traceScreenProbesPass
            .Run(graph, header, usesRayQuery: usesRayQuery)
            .Reads(
                frameConstants,
                Constants,
                instances,
                visibleClusters,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal
            )
            .Reads(targets.Hzb, 0)
            .Reads(_screenProbes[_current])
            .Writes(_screenRadiance);
        UseForCards(graph);
        UseProbes(graph);
        cards.UseForReading(graph);
        field.UseForSampling(graph);
        lighting.UseForCards(graph);
        translucency.UseForSampling(graph);
        traceScreenProbesRun.Dispatch((uint)probeCount);

        _reprojectScreenProbesPass
            .Run(graph, header)
            .Reads(
                frameConstants,
                Constants,
                lighting.Constants,
                _screenProbes[_current],
                _screenProbes[1 - _current],
                _screenFiltered[1 - _current]
            )
            .Writes(_screenRadiance)
            .Dispatch(Groups(_screenProbesX * ScreenTexels, 8), Groups(_screenProbesY * ScreenTexels, 8));

        _occlusionPass
            .Run(graph, header)
            .Reads(frameConstants, Constants, lighting.Constants, targets.Depth, targets.GBufferNormal)
            .Writes(_occlusionRaw)
            .Dispatch(Groups(targets.Width, 8), Groups(targets.Height, 8));

        _blurOcclusionPass
            .Run(graph, header)
            .Reads(frameConstants, Constants, lighting.Constants, targets.Depth, _occlusionRaw)
            .Writes(_occlusion)
            .Dispatch(Groups(targets.Width, 8), Groups(targets.Height, 8));

        _filterScreenProbesPass
            .Run(graph, header)
            .Reads(Constants, lighting.Constants, _screenProbes[_current], _screenRadiance)
            .Writes(_screenFiltered[_current], _screenIrradiance)
            .Dispatch((uint)probeCount);

        if (!_hasReflections)
            return;

        GpuPassRun traceReflectionsRun = _traceReflectionsPass
            .Run(graph, header, usesRayQuery: usesRayQuery)
            .Reads(
                frameConstants,
                Constants,
                instances,
                visibleClusters,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferSurface
            )
            .Reads(targets.Hzb, 0)
            .Writes(_reflectionTraced);
        UseForCards(graph);
        UseProbes(graph);
        cards.UseForReading(graph);
        field.UseForSampling(graph);
        lighting.UseForCards(graph);
        translucency.UseForSampling(graph);
        traceReflectionsRun.Dispatch(
            Groups((targets.Width + _reflectionStride - 1) / _reflectionStride, 8),
            Groups((targets.Height + _reflectionStride - 1) / _reflectionStride, 8)
        );

        _resolveReflectionsPass
            .Run(graph, header)
            .Reads(
                frameConstants,
                Constants,
                lighting.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferSurface,
                _reflectionTraced
            )
            .Writes(_reflectionResolved)
            .Dispatch(Groups(targets.Width, 8), Groups(targets.Height, 8));

        _reprojectReflectionsPass
            .Run(graph, header)
            .Reads(
                frameConstants,
                Constants,
                lighting.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferSurface,
                targets.Velocity,
                _reflectionResolved,
                _reflections[1 - _current]
            )
            .Writes(_reflections[_current])
            .Dispatch(Groups(targets.Width, 8), Groups(targets.Height, 8));
    }

    /// <summary>
    /// Says a pass reads the screen probes and the world probes: what the lighting declares.
    /// </summary>
    public void UseForSampling(RenderGraph graph)
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_screenProbes[_current], GpuAccess.ShaderRead);
        graph.Use(_screenFiltered[_current], GpuAccess.ShaderRead);
        graph.Use(_screenIrradiance, GpuAccess.ShaderRead);
        graph.Use(_reflections[_current], GpuAccess.ShaderRead);
        graph.Use(_occlusion, GpuAccess.ShaderRead);
        UseProbes(graph);
    }

    /// <summary>
    /// Says a pass reads the lit cards: what a pass that shades a ray's end declares.
    /// </summary>
    public void UseForCards(RenderGraph graph)
    {
        graph.Use(_litPool, GpuAccess.ShaderRead);
        graph.Use(_litSlots, GpuAccess.ShaderRead);
    }

    private static uint Groups(int count, int size = Threads) => (uint)((count + size - 1) / size);

    private void UseProbes(RenderGraph graph)
    {
        graph.Use(_probeStates, GpuAccess.ShaderRead);
        graph.Use(_probeRadiance, GpuAccess.ShaderRead);
        graph.Use(_probeDistance, GpuAccess.ShaderRead);
        graph.Use(_probeIrradiance, GpuAccess.ShaderRead);
    }

    private void Upload(RenderGraph graph, GpuBuffer buffer, ReadOnlySpan<byte> bytes)
    {
        GpuUpload upload = gpu.BeginUpload(bytes.Length);
        bytes.CopyTo(upload.Bytes);
        graph.Use(buffer, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, buffer, 0, (ulong)bytes.Length);
    }

    /// <summary>
    /// The slots to the instances that matter most: a slot stays with its instance while it still matters, and the
    /// freed ones go to the newcomers, which are lit first.
    /// </summary>
    private void AssignSlots(ReadOnlySpan<GpuInstance> instances, Vector3 cameraPosition)
    {
        _candidates.Clear();
        for (int i = 0; i < instances.Length; i++)
        {
            GpuInstance row = instances[i];
            if (row.ClusterCount == 0 || row.Capture == 0)
                continue;

            float distance = Math.Max(Vector3.Distance(row.BoundsCenter, cameraPosition) - row.BoundsRadius, 1);
            _candidates.Add((row.BoundsRadius / distance, i));
        }

        if (_candidates.Count > LitSlots)
        {
            _candidates.Sort((a, b) => b.Importance.CompareTo(a.Importance));
            _candidates.RemoveRange(LitSlots, _candidates.Count - LitSlots);
        }

        Array.Clear(_isWanted);
        foreach ((_, int index) in _candidates)
        {
            _isWanted[index] = true;
        }

        for (int slot = 0; slot < LitSlots; slot++)
        {
            if (!_isSlotUsed[slot])
                continue;

            int index = (int)_slots[slot].Instance;
            if (_isWanted[index] && instances[index].Capture == _slots[slot].Capture)
                continue;

            _isSlotUsed[slot] = false;
            _slotOfInstance[index] = 0;
            _slots[slot] = default;
            _freeSlots.Push(slot);
            _areSlotsDirty = true;
        }

        foreach ((_, int index) in _candidates)
        {
            if (_slotOfInstance[index] != 0 || !_freeSlots.TryPop(out int slot))
                continue;

            _isSlotUsed[slot] = true;
            _slotOfInstance[index] = (uint)slot + 1;
            _slots[slot] = new GpuLitSlot { Instance = (uint)index, Capture = instances[index].Capture };
            _pendingSlots.Enqueue((uint)slot);
            _areSlotsDirty = true;
        }
    }

    /// <summary>
    /// The frame's batch of slots to light: the newly given ones first, then each in turn.
    /// </summary>
    private void PickBatch()
    {
        _batchCount = 0;
        while (_batchCount < _slotsPerFrame && _pendingSlots.TryDequeue(out uint pending))
        {
            if (_isSlotUsed[pending])
                _batch[_batchCount++] = pending;
        }

        for (int tried = 0; tried < LitSlots && _batchCount < _slotsPerFrame; tried++)
        {
            int slot = _cursor;
            _cursor = (_cursor + 1) % LitSlots;
            if (!_isSlotUsed[slot] || Array.IndexOf(_batch, (uint)slot, 0, _batchCount) >= 0)
                continue;

            _batch[_batchCount++] = (uint)slot;
        }
    }

    private Result<GpuTexture> Make(string name, int width, int height, GpuFormat format) =>
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

    private void Destroy(RenderGraph? graph)
    {
        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [
                    _screenRadiance,
                    _screenFiltered[0],
                    _screenFiltered[1],
                    _reflectionTraced,
                    _reflectionResolved,
                    _reflections[0],
                    _reflections[1],
                    _occlusionRaw,
                    _occlusion,
                ]
        )
        {
            if (texture.IsNone)
                continue;

            graph?.Forget(texture);
            gpu.Destroy(texture);
        }

        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[_screenProbes[0], _screenProbes[1], _screenIrradiance])
        {
            if (buffer.IsNone)
                continue;

            graph?.Forget(buffer);
            gpu.Destroy(buffer);
        }

        _screenRadiance = default;
        _screenFiltered[0] = default;
        _screenFiltered[1] = default;
        _occlusionRaw = default;
        _occlusion = default;
        _reflectionTraced = default;
        _reflectionResolved = default;
        _reflections[0] = default;
        _reflections[1] = default;
        _screenProbes[0] = default;
        _screenProbes[1] = default;
        _screenIrradiance = default;
    }
}

/// <summary>
/// What the global illumination's passes read, the twin of <c>Scene/Probes.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GiConstants
{
    public const int Bytes = 224;

    public ProbeOrigins ProbeOrigins;

    public Vector4 ProbeSpacings;

    public Vector4 FallbackIrradiance;

    public uint LitPool;

    public uint LitSlots;

    public uint SlotInstances;

    public uint SlotLights;

    public uint ProbeStates;

    public uint ProbeList;

    public uint ProbeRadiance;

    public uint ProbeDistance;

    public uint ProbeIrradiance;

    public uint CardRadiosity;

    public uint ScreenRadiance;

    public uint ScreenRadiancePrevious;

    public uint ScreenProbes;

    public uint ScreenProbesPrevious;

    public uint ScreenIrradiance;

    public uint ScreenProbesX;

    public uint ScreenProbesY;

    public uint SlotBatch;

    public uint ProbeBudget;

    public uint FrameIndex;

    public float RayRange;

    public uint ScreenFiltered;

    public uint ReflectionTraced;

    public uint ReflectionResolved;

    public uint Reflections;

    public uint ReflectionsPrevious;

    public uint HasReflections;

    public uint OcclusionRaw;

    public uint Occlusion;

    public uint ReflectionStride;
}

/// <summary>
/// A level's window: the probe, counted in that level's probes, at its corner.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ProbeOrigin(int X, int Y, int Z, int W);

[InlineArray(GlobalIllumination.ProbeLevels)]
internal struct ProbeOrigins
{
    private ProbeOrigin _first;
}

/// <summary>
/// A lit card's slot, the twin of <c>Scene/Probes.slang</c>'s: the instance it lights, that instance's capture
/// row, and how many times it was lit, 0 the first time.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuLitSlot
{
    public const int Bytes = 16;

    public uint Instance;

    public uint Capture;

    public uint Frames;

    public uint Padding;
}
