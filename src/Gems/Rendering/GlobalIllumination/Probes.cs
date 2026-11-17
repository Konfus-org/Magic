using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// The probe grid of diffuse light: cascades of a box of probes centred on the camera at whole cells, each with cells
/// twice as wide as the one inside it. Each view has its own grid.
/// </summary>
/// <remarks>
/// Probes get rays in turn within each frame's budget, more while their light still changes, and blend what the rays
/// trace into their light and distances. The shaders place the grid on the camera. A probe keeps its light while the
/// moving grid keeps its cell, and starts afresh in a new one. What the probes hold carries from frame to frame, from
/// the first update on.
/// </remarks>
internal sealed class Probes(AssetManager assets)
{
    /// <summary>
    /// The most probes of a cascade along x and z, and up y: the depth texture lays them out in a row of tiles along x
    /// and up, as many as its 16384 texels hold, and a row for each cascade.
    /// </summary>
    private const int MaxProbesAcross = 48;
    private const int MaxProbesUp = 16;

    /// <summary>
    /// The cascades of the grid.
    /// </summary>
    private const int Cascades = 3;

    /// <summary>
    /// The rays all the probes trace in a frame, and the most rays a probe traces in a frame, which it traces before it
    /// first blends its rays: the twins of <c>UpdateProbes.slang</c>'s.
    /// </summary>
    private const int RayBudget = 131072;
    private const int MaxProbeRays = 256;

    /// <summary>
    /// The twins of the renderer's <c>Light/Probes.slang</c>'s: the texels along a side of a probe's map of light, the
    /// side of a probe's tile of distances, and a probe's bytes.
    /// </summary>
    private const int IrradianceTexels = 8;
    private const int DepthTile = 18;
    private const int ProbeBytes = 272;

    /// <summary>
    /// The bytes of a cell of the light grid, a bit for each of the renderer's most lights: the twin of
    /// <c>Traced.slang</c>'s.
    /// </summary>
    private const int LightCellBytes = 1024 / 8;

    /// <summary>
    /// The light grid's cells to a probe cell of the finest cascade: the twin of <c>Traced.slang</c>'s
    /// <c>LightCellsPerProbe</c> along each axis, cubed.
    /// </summary>
    private const int LightCellsPerProbe = 2 * 2 * 2;

    /// <summary>
    /// The rows of the light table, a group of the bin pass each: the twin of <c>Scene/Lights.slang</c>'s.
    /// </summary>
    private const int LightTableRows = 1024;

    private const int RayBytes = 16;
    private const int VarianceBytes = 16;

    private readonly ShaderEntry _askShader = PassShaders.GetEntry(assets, "UpdateProbes", "AskProbeRays");
    private readonly ShaderEntry _planShader = PassShaders.GetEntry(assets, "UpdateProbes", "PlanProbeRays");
    private readonly ShaderEntry _listShader = PassShaders.GetEntry(assets, "UpdateProbes", "ListProbeRays");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "UpdateProbes", "BlendProbes");
    private readonly ShaderEntry _clearLightsShader = PassShaders.GetEntry(assets, "BinLights", "ClearLightCells");
    private readonly ShaderEntry _binLightsShader = PassShaders.GetEntry(assets, "BinLights", "BinLights");

    /// <summary>
    /// For each view, by its number: its grid's probes along x and z, and up y, and the metres between them.
    /// </summary>
    private readonly List<(int Across, int Up, float Spacing)> _grids = [];

    /// <summary>
    /// The trace's entry, generic over the sky, which the renderer specialises on the frame's sky.
    /// </summary>
    public ShaderEntry TraceShader { get; } = PassShaders.GetEntry(assets, "UpdateProbes", "TraceProbes");

    public ImmutableArray<ShaderEntry> Shaders =>
        [_askShader, _planShader, _listShader, _blendShader, _clearLightsShader, _binLightsShader];

    /// <summary>
    /// The frames that a grid with cascades of <paramref name="size"/> takes to give each of its probes its first rays,
    /// which are the most a probe traces.
    /// </summary>
    public static int GetSweepFrames((int Across, int Up) size)
    {
        (int across, int up) = Clamp(size);
        long rays = (long)across * up * across * Cascades * MaxProbeRays;

        return (int)((rays + RayBudget - 1) / RayBudget);
    }

    /// <summary>
    /// Asks for the buffers and depth texture of the grid of the graph's view, each cascade at <paramref name="size"/>,
    /// with the probes of the finest <paramref name="spacing"/> metres apart.
    /// </summary>
    /// <remarks>
    /// The size goes from 2 probes a side up to the most, and the spacing from 0.1 metres. The probes start at none
    /// while they hold nothing or the size or spacing changed.
    /// </remarks>
    public Result<(ProbeGridConstants Grid, ProbeUpdateConstants Update)> Prepare(
        RenderGraph graph,
        bool hasHistory,
        (int Across, int Up) size,
        float spacing
    )
    {
        size = Clamp(size);
        spacing = MathF.Max(spacing, 0.1f);

        // What the view's grid keeps from frame to frame, by the view's number.
        while (_grids.Count <= graph.View)
        {
            _grids.Add(default);
        }

        (int across, int up) = size;
        hasHistory &= _grids[graph.View] == (across, up, spacing);
        _grids[graph.View] = (across, up, spacing);
        long cells = (long)across * up * across;
        long probes = cells * Cascades;
        // PROTO: the light grid's cells, a few for each probe of the finest cascade, follow the probes.
        Result<GpuBuffer> probeTable = graph.Buffer(
            new("Probes", (probes * ProbeBytes) + (cells * LightCellsPerProbe * LightCellBytes))
        );
        Result<GpuBuffer> rays = graph.Buffer(new("Probe rays", RayBudget * RayBytes));
        Result<GpuBuffer> list = graph.Buffer(new("Probe ray list", RayBudget * 4));
        Result<GpuBuffer> arguments = graph.Buffer(
            new("Probe ray arguments", 16, BufferUsages.Storage | BufferUsages.Indirect)
        );
        Result<GpuBuffer> counts = graph.Buffer(new("Probe ray counts", ((probes * 2) + 1) * 4));
        Result<GpuBuffer> variance = graph.Buffer(
            new("Probe variance", probes * IrradianceTexels * IrradianceTexels * VarianceBytes)
        );
        Result<GpuTexture> depth = graph.Texture(
            new("Probe depth", DepthTile * across * up, DepthTile * across * Cascades, GpuFormat.R16G16Sfloat)
        );
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        Result asked = Result.FindFailure(probeTable, rays, list, arguments, counts, variance, depth, sampler);
        if (!asked.Ok)
            return Result.Failure(asked.Error);

        ProbeGridConstants grid = new()
        {
            DimensionX = (uint)across,
            DimensionY = (uint)up,
            DimensionZ = (uint)across,
            Cascades = Cascades,
            Probes = probeTable.Value,
            Depth = depth.Value,
            DepthSampler = sampler.Value,
            CellSize = new Vector3(spacing),
        };
        ProbeUpdateConstants update = new()
        {
            Rays = rays.Value,
            RayList = list.Value,
            RayArguments = arguments.Value,
            RayCounts = counts.Value,
            Variance = variance.Value,
            MaxDistance = spacing * 1.5f,
        };

        // The probes start at none on the first update.
        if (!hasHistory)
            graph.Clear(grid.Probes);

        return Result.Success((grid, update));
    }

    /// <summary>
    /// Traces the rays that the probes ask for through the world, and blends their light into the probes.
    /// </summary>
    /// <remarks>
    /// Probes get rays in turn while they fit in the frame's budget. The trace's arguments keep the probe that the next
    /// frame starts at. The lights and the sky light the rays. First, each light is put into the cells of the light
    /// grid that its reach touches, which every ray reads.
    /// </remarks>
    public void Record(in GIView view, in ProbeGridConstants grid, in ProbeUpdateConstants update)
    {
        RenderGraph graph = view.Graph;
        GpuConstants header = view.Header;
        uint cellCount = grid.DimensionX * grid.DimensionY * grid.DimensionZ;
        uint probeCount = cellCount * grid.Cascades;
        graph
            .Compute("Clear light cells", _clearLightsShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame)
            .Writes(grid.Probes)
            .DispatchOver((int)cellCount * LightCellsPerProbe * LightCellBytes / sizeof(uint), 1);
        graph
            .Compute("Bin lights", _binLightsShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame, view.Gpu.LightTable)
            .Writes(grid.Probes)
            .Dispatch(LightTableRows);
        graph
            .Compute("Ask probe rays", _askShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame, view.Constants, update.Variance)
            .Writes(grid.Probes, update.RayCounts)
            .Dispatch(probeCount);
        graph
            .Compute("Plan probe rays", _planShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame, view.Constants)
            .Writes(update.RayCounts, update.RayArguments)
            .Dispatch(1);
        graph
            .Compute("List probe rays", _listShader, header, RenderStages.Light)
            .Reads(view.Constants, update.RayCounts)
            .Writes(update.RayList)
            .Dispatch(probeCount);
        graph
            .Compute("Trace probes", TraceShader with { TypeArguments = view.SkyType }, header, RenderStages.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, grid.Probes, update.RayList, update.RayCounts, grid.Depth)
            .Writes(update.Rays)
            .Indirect(update.RayArguments)
            .DispatchIndirect(update.RayArguments, 0);
        graph
            .Compute("Blend probes", _blendShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame, view.Constants, update.Rays, update.RayCounts)
            .Writes(grid.Probes, update.Variance, grid.Depth)
            .Dispatch(probeCount);
    }

    private static (int Across, int Up) Clamp((int Across, int Up) size) =>
        (Math.Clamp(size.Across, 2, MaxProbesAcross), Math.Clamp(size.Up, 2, MaxProbesUp));
}
