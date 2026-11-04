using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// What lights the world indirectly: the lit cards, a pool of slots each holding an instance's capture lit, given
/// to the instances that matter most to the view; the world probes, a clipmap of spheres of radiance around the
/// camera that trace the field and read the cards; the screen probes, one a tile of pixels, that trace the screen
/// and the field and end in the world probes; the sharp reflections, a ray a pixel at half the output; and the passes
/// that keep each up to date, a budget of each a frame.
/// </summary>
internal sealed class GlobalIllumination(IGraphicsApi gpu) : IDisposable
{
    public const int LitSlots = 256;
    public const int ProbeLevels = 4;
    public const int ProbesPerAxis = 16;
    public const int ProbesPerLevel = ProbesPerAxis * ProbesPerAxis * ProbesPerAxis;
    public const int ProbeBudget = 256;

    private const int LitSlotTexels = 256;
    private const int LitPoolTexels = 4096;
    private const int LitLightsPerSlot = 64;
    private const int SlotsPerFrame = 8;
    private const int AssignPeriod = 16;
    private const int ProbeTexels = 16;
    private const int ProbeIrradianceTexels = 8;
    private const int ProbesPerAtlasRow = 64;
    private const int ScreenTile = 16;
    private const int ScreenTexels = 8;
    private const int Threads = 64;
    private const float FinestSpacing = 1;
    private const float RayRange = 64;

    private readonly uint[] _slotOfInstance = new uint[InstanceTable.Capacity];
    private readonly LitSlotRow[] _slots = new LitSlotRow[LitSlots];
    private readonly bool[] _isSlotUsed = new bool[LitSlots];
    private readonly bool[] _isWanted = new bool[InstanceTable.Capacity];
    private readonly Stack<int> _freeSlots = new();
    private readonly Queue<uint> _pendingSlots = new();
    private readonly List<(float Importance, int Index)> _candidates = [];
    private readonly uint[] _batch = new uint[SlotsPerFrame];
    private readonly GpuTexture[] _screenFiltered = new GpuTexture[2];
    private readonly GpuTexture[] _reflections = new GpuTexture[2];
    private GpuTexture _reflectionTraced;
    private GpuTexture _reflectionResolved;
    private readonly GpuBuffer[] _screenProbes = new GpuBuffer[2];
    private GpuTexture _litPool;
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
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)[_litPool, _probeRadiance, _probeDistance, _probeIrradiance]
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
            ("Lit slot instances", (long)LitSlots * LitSlotRow.Bytes),
            ("Lit slot batch", (long)SlotsPerFrame * 4),
            ("Lit slot lights", (long)LitSlots * (LitLightsPerSlot + 1) * 4),
            ("Probe states", (long)ProbeLevels * ProbesPerLevel * 16),
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
            Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)[litPool, radiance, distance, irradiance]
        )
        {
            if (!texture.Ok)
                return Result.Failure($"a global illumination table was not made: {texture.Error}");
        }

        (_litPool, _probeRadiance, _probeDistance, _probeIrradiance) = (
            litPool.Value,
            radiance.Value,
            distance.Value,
            irradiance.Value
        );

        return Result.Success();
    }

    /// <summary>
    /// Makes the screen probes for an output size, the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height, RenderGraph graph)
    {
        Destroy(graph);
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
                    Bytes = (long)count * 32,
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
            (width + 1) / 2,
            (height + 1) / 2,
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
        foreach (
            Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)
                [reflectionTraced, reflectionResolved, reflections0, reflections1]
        )
        {
            if (!texture.Ok)
                return Result.Failure($"a reflection texture was not made: {texture.Error}");
        }

        (_reflectionTraced, _reflectionResolved, _reflections[0], _reflections[1]) = (
            reflectionTraced.Value,
            reflectionResolved.Value,
            reflections0.Value,
            reflections1.Value
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
            ScreenRadiance = _screenRadiance.Id,
            ScreenRadiancePrevious = _screenFiltered[1 - _current].Id,
            ScreenProbes = _screenProbes[_current].Id,
            ScreenProbesPrevious = _screenProbes[1 - _current].Id,
            ScreenIrradiance = _screenIrradiance.Id,
            ScreenProbesX = (uint)_screenProbesX,
            ScreenProbesY = (uint)_screenProbesY,
            LitSlotCount = LitSlots,
            SlotBatch = _slotBatch.Id,
            SlotsPerFrame = (uint)_batchCount,
            ProbeBudget = ProbeBudget,
            FrameIndex = frameIndex,
            RayRange = RayRange,
            ScreenFiltered = _screenFiltered[_current].Id,
            ReflectionTraced = _reflectionTraced.Id,
            ReflectionResolved = _reflectionResolved.Id,
            Reflections = _reflections[_current].Id,
            ReflectionsPrevious = _reflections[1 - _current].Id,
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
    /// The cards, the world probes and the screen probes brought up to date, after the GBuffer, the depth pyramid
    /// and the sun's pages, before the lighting reads them.
    /// </summary>
    public void Record(
        RenderGraph graph,
        RendererPipelines pipelines,
        GpuBuffer frameConstants,
        GpuBuffer instances,
        GpuBuffer visibleClusters,
        SunShadows shadows,
        Lighting lighting,
        DistanceField field,
        Translucency translucency,
        CaptureTable captures,
        FrameTargets targets
    )
    {
        GpuConstants header = new(frameConstants.Id, shadows.Constants.Id, lighting.Constants.Id, field.Constants.Id);
        if (_batchCount > 0)
        {
            graph.BeginComputePass("GI: list card lights");
            graph.Use(frameConstants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(instances, GpuAccess.ShaderRead);
            graph.Use(_slotBatch, GpuAccess.ShaderRead);
            graph.Use(_slotInstances, GpuAccess.ShaderRead);
            graph.Use(_slotLights, GpuAccess.ShaderWrite);
            lighting.UseForCards(graph);
            translucency.UseForSampling(graph);
            translucency.UseForSampling(graph);
            graph.Recorder.BindPipeline(pipelines.LitCardLights);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch((uint)_batchCount);
            graph.EndPass();

            graph.BeginComputePass("GI: light cards");
            graph.Use(frameConstants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(instances, GpuAccess.ShaderRead);
            graph.Use(captures.Buffer, GpuAccess.ShaderRead);
            graph.Use(_slotBatch, GpuAccess.ShaderRead);
            graph.Use(_slotInstances, GpuAccess.ShaderRead);
            graph.Use(_slotLights, GpuAccess.ShaderRead);
            graph.Use(_litPool, GpuAccess.ShaderWrite);
            UseProbes(graph);
            captures.UseAll(graph);
            shadows.UseForSampling(graph);
            lighting.UseForCards(graph);
            translucency.UseForSampling(graph);
            translucency.UseForSampling(graph);
            graph.Recorder.BindPipeline(pipelines.LitCardShade);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch(LitSlotTexels / 8, LitSlotTexels / 8, (uint)_batchCount);
            graph.EndPass();
        }

        int cells = ProbeLevels * ProbesPerLevel;
        graph.BeginComputePass("GI: scroll probes");
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(_probeStates, GpuAccess.ShaderWrite);
        graph.Use(_probeList, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.WorldProbeScroll);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(cells));
        graph.EndPass();

        graph.BeginComputePass("GI: select probes");
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(_probeStates, GpuAccess.ShaderRead);
        graph.Use(_probeList, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.WorldProbeSelect);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(cells));
        graph.EndPass();

        graph.BeginComputePass("GI: trace probes");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(instances, GpuAccess.ShaderRead);
        graph.Use(captures.Buffer, GpuAccess.ShaderRead);
        graph.Use(_probeList, GpuAccess.ShaderRead);
        graph.Use(_probeStates, GpuAccess.ShaderWrite);
        graph.Use(_probeRadiance, GpuAccess.ShaderWrite);
        graph.Use(_probeDistance, GpuAccess.ShaderWrite);
        graph.Use(_probeIrradiance, GpuAccess.ShaderWrite);
        UseCards(graph);
        captures.UseAll(graph);
        field.UseForSampling(graph);
        lighting.UseForCards(graph);
        translucency.UseForSampling(graph);
        graph.Recorder.BindPipeline(pipelines.WorldProbeTrace);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(ProbeBudget);
        graph.EndPass();

        int probeCount = _screenProbesX * _screenProbesY;
        graph.BeginComputePass("GI: place screen probes");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(_screenProbes[_current], GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.ScreenProbePlace);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(_screenProbesX, 8), Groups(_screenProbesY, 8));
        graph.EndPass();

        graph.BeginComputePass("GI: trace screen probes");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(instances, GpuAccess.ShaderRead);
        graph.Use(visibleClusters, GpuAccess.ShaderRead);
        graph.Use(captures.Buffer, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(targets.Hzb, GpuAccess.ShaderRead, 0);
        graph.Use(_screenProbes[_current], GpuAccess.ShaderRead);
        graph.Use(_screenRadiance, GpuAccess.ShaderWrite);
        UseCards(graph);
        UseProbes(graph);
        captures.UseAll(graph);
        field.UseForSampling(graph);
        lighting.UseForCards(graph);
        translucency.UseForSampling(graph);
        graph.Recorder.BindPipeline(pipelines.ScreenProbeTrace);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch((uint)probeCount);
        graph.EndPass();

        graph.BeginComputePass("GI: reproject screen probes");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(_screenProbes[_current], GpuAccess.ShaderRead);
        graph.Use(_screenProbes[1 - _current], GpuAccess.ShaderRead);
        graph.Use(_screenFiltered[1 - _current], GpuAccess.ShaderRead);
        graph.Use(_screenRadiance, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.ScreenProbeTemporal);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(_screenProbesX * ScreenTexels, 8), Groups(_screenProbesY * ScreenTexels, 8));
        graph.EndPass();

        graph.BeginComputePass("GI: filter screen probes");
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(_screenProbes[_current], GpuAccess.ShaderRead);
        graph.Use(_screenRadiance, GpuAccess.ShaderRead);
        graph.Use(_screenFiltered[_current], GpuAccess.ShaderWrite);
        graph.Use(_screenIrradiance, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.ScreenProbeFilter);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch((uint)probeCount);
        graph.EndPass();

        graph.BeginComputePass("Reflections: trace");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(instances, GpuAccess.ShaderRead);
        graph.Use(visibleClusters, GpuAccess.ShaderRead);
        graph.Use(captures.Buffer, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferSurface, GpuAccess.ShaderRead);
        graph.Use(targets.Hzb, GpuAccess.ShaderRead, 0);
        graph.Use(_reflectionTraced, GpuAccess.ShaderWrite);
        UseCards(graph);
        UseProbes(graph);
        captures.UseAll(graph);
        field.UseForSampling(graph);
        lighting.UseForCards(graph);
        translucency.UseForSampling(graph);
        graph.Recorder.BindPipeline(pipelines.ReflectionTrace);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups((targets.Width + 1) / 2, 8), Groups((targets.Height + 1) / 2, 8));
        graph.EndPass();

        graph.BeginComputePass("Reflections: resolve");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferSurface, GpuAccess.ShaderRead);
        graph.Use(_reflectionTraced, GpuAccess.ShaderRead);
        graph.Use(_reflectionResolved, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.ReflectionResolve);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(targets.Width, 8), Groups(targets.Height, 8));
        graph.EndPass();

        graph.BeginComputePass("Reflections: reproject");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferSurface, GpuAccess.ShaderRead);
        graph.Use(_reflectionResolved, GpuAccess.ShaderRead);
        graph.Use(_reflections[1 - _current], GpuAccess.ShaderRead);
        graph.Use(_reflections[_current], GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.ReflectionTemporal);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(targets.Width, 8), Groups(targets.Height, 8));
        graph.EndPass();
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
        UseProbes(graph);
    }

    private static uint Groups(int count, int size = Threads) => (uint)((count + size - 1) / size);

    private void UseProbes(RenderGraph graph)
    {
        graph.Use(_probeStates, GpuAccess.ShaderRead);
        graph.Use(_probeRadiance, GpuAccess.ShaderRead);
        graph.Use(_probeDistance, GpuAccess.ShaderRead);
        graph.Use(_probeIrradiance, GpuAccess.ShaderRead);
    }

    private void UseCards(RenderGraph graph)
    {
        graph.Use(_litPool, GpuAccess.ShaderRead);
        graph.Use(_litSlots, GpuAccess.ShaderRead);
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
            _slots[slot] = new LitSlotRow { Instance = (uint)index, Capture = instances[index].Capture };
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
        while (_batchCount < SlotsPerFrame && _pendingSlots.TryDequeue(out uint pending))
        {
            if (_isSlotUsed[pending])
                _batch[_batchCount++] = pending;
        }

        for (int tried = 0; tried < LitSlots && _batchCount < SlotsPerFrame; tried++)
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
    public const int Bytes = 216;

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

    public uint Padding2;

    public uint ScreenRadiance;

    public uint ScreenRadiancePrevious;

    public uint ScreenProbes;

    public uint ScreenProbesPrevious;

    public uint ScreenIrradiance;

    public uint ScreenProbesX;

    public uint ScreenProbesY;

    public uint LitSlotCount;

    public uint SlotBatch;

    public uint SlotsPerFrame;

    public uint ProbeBudget;

    public uint FrameIndex;

    public float RayRange;

    public uint ScreenFiltered;

    public uint ReflectionTraced;

    public uint ReflectionResolved;

    public uint Reflections;

    public uint ReflectionsPrevious;

    public uint Padding0;

    public uint Padding1;
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
/// A lit card's slot, the twin of <c>Scene/Probes.slang</c>'s LitSlot: the instance it lights and that instance's
/// capture row.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct LitSlotRow
{
    public const int Bytes = 16;

    public uint Instance;

    public uint Capture;

    public uint Padding0;

    public uint Padding1;
}
