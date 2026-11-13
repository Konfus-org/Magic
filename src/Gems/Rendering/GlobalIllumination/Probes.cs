using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// The probe grid of diffuse light: a box of probes over the traced world's box. Each view has its own grid.
/// </summary>
/// <remarks>
/// Probes get rays in turn within each frame's budget, more while their light still changes, and blend what the rays
/// trace into their light and distances. The box follows the world's slowly, so the probes' light moves with it. What
/// the probes hold carries from frame to frame, from the first update on.
/// </remarks>
internal sealed class Probes(AssetManager assets)
{
    /// <summary>
    /// The most probes along x and z, and up y: the depth texture lays them out in a row of tiles along x and up, as
    /// many as its 16384 texels hold.
    /// </summary>
    private const int MaxProbesAcross = 48;
    private const int MaxProbesUp = 16;

    /// <summary>
    /// The rays all the probes trace in a frame, and the texels along a probe's irradiance tile: the twins of
    /// <c>UpdateProbes.slang</c>'s.
    /// </summary>
    private const int RayBudget = 131072;
    private const int IrradianceTexels = 8;

    /// <summary>
    /// The twins of the renderer's <c>Light/Probes.slang</c>'s.
    /// </summary>
    private const int DepthTile = 18;
    private const int ProbeBytes = 80;

    private const int RayBytes = 16;
    private const int VarianceBytes = 16;

    /// <summary>
    /// The metres the grid reaches past the traced world's box on every side.
    /// </summary>
    /// <remarks>
    /// It is also how far the grid moves towards that box each frame once the probes hold light, so the grid does not
    /// shake their light as instances come and go.
    /// </remarks>
    private const float BoxMargin = 1;
    private const float BoxBlend = 0.01f;

    private readonly ShaderEntry _askShader = PassShaders.GetEntry(assets, "UpdateProbes", "AskProbeRays");
    private readonly ShaderEntry _planShader = PassShaders.GetEntry(assets, "UpdateProbes", "PlanProbeRays");
    private readonly ShaderEntry _listShader = PassShaders.GetEntry(assets, "UpdateProbes", "ListProbeRays");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "UpdateProbes", "BlendProbes");

    /// <summary>
    /// For each view, by its number: the box its grid spans, where the last frame placed it, and its probes along x
    /// and z, and up y.
    /// </summary>
    private readonly List<((Vector3 Min, Vector3 Max) Box, (int Across, int Up) Size)> _grids = [];

    /// <summary>
    /// The trace's entry, generic over the sky, which the renderer specialises on the frame's sky.
    /// </summary>
    public ShaderEntry TraceShader { get; } = PassShaders.GetEntry(assets, "UpdateProbes", "TraceProbes");

    public ImmutableArray<ShaderEntry> Shaders => [_askShader, _planShader, _listShader, _blendShader];

    /// <summary>
    /// Asks for the buffers and depth texture of the grid of the graph's view at <paramref name="size"/>, and places
    /// the grid over <paramref name="box"/> with a margin.
    /// </summary>
    /// <remarks>
    /// The size goes from 2 probes a side up to the most. The probes start at none while they hold nothing or the size
    /// changed. The grid moves to the box at once when the probes start afresh, else a little nearer each frame.
    /// </remarks>
    public Result<(ProbeGridConstants Grid, ProbeUpdateConstants Update)> Prepare(
        RenderGraph graph,
        bool hasHistory,
        (Vector3 Min, Vector3 Max) box,
        (int Across, int Up) size
    )
    {
        size = (Math.Clamp(size.Across, 2, MaxProbesAcross), Math.Clamp(size.Up, 2, MaxProbesUp));

        // What the view's grid keeps from frame to frame, by the view's number.
        while (_grids.Count <= graph.View)
        {
            _grids.Add(default);
        }

        (Vector3 Min, Vector3 Max) placed = _grids[graph.View].Box;
        hasHistory &= size == _grids[graph.View].Size;
        _grids[graph.View] = (placed, size);
        (int across, int up) = size;
        long probes = (long)across * up * across;
        Result<GpuBuffer> probeTable = graph.Buffer(new("Probes", probes * ProbeBytes));
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
            new("Probe depth", DepthTile * across * up, DepthTile * across, GpuFormat.R16G16Sfloat)
        );
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        Result asked = Result.FindFailure(probeTable, rays, list, arguments, counts, variance, depth, sampler);
        if (!asked.Ok)
            return Result.Failure(asked.Error);

        (Vector3 Min, Vector3 Max) target = (box.Min - new Vector3(BoxMargin), box.Max + new Vector3(BoxMargin));
        placed = hasHistory
            ? (Vector3.Lerp(placed.Min, target.Min, BoxBlend), Vector3.Lerp(placed.Max, target.Max, BoxBlend))
            : target;
        _grids[graph.View] = (placed, size);
        Vector3 cellSize = (placed.Max - placed.Min) / new Vector3(across - 1, up - 1, across - 1);
        ProbeGridConstants grid = new()
        {
            DimensionX = (uint)across,
            DimensionY = (uint)up,
            DimensionZ = (uint)across,
            Probes = probeTable.Value,
            Depth = depth.Value,
            DepthSampler = sampler.Value,
            Origin = placed.Min,
            CellSize = cellSize,
        };
        ProbeUpdateConstants update = new()
        {
            Rays = rays.Value,
            RayList = list.Value,
            RayArguments = arguments.Value,
            RayCounts = counts.Value,
            Variance = variance.Value,
            MaxDistance = MathF.Max(cellSize.X, MathF.Max(cellSize.Y, cellSize.Z)) * 1.5f,
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
    /// frame starts at. The lights and the sky light the rays.
    /// </remarks>
    public void Record(in GIView view, in ProbeGridConstants grid, in ProbeUpdateConstants update)
    {
        RenderGraph graph = view.Graph;
        GpuConstants header = view.Header;
        uint probeCount = grid.DimensionX * grid.DimensionY * grid.DimensionZ;
        graph
            .Compute("Ask probe rays", _askShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame, view.Constants, grid.Probes, update.Variance)
            .Writes(update.RayCounts)
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
}
