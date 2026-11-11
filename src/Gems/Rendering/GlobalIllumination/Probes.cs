using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// The probe grid of diffuse light: a box of probes over the traced world's box, given rays in turn within each frame's
/// budget, more as their light still changes, traced, and blended into each probe's light and distances. The box
/// follows the world's slowly, so the probes' light moves with it. What the probes hold carries from frame to frame,
/// from the first update on.
/// </summary>
internal sealed class Probes(AssetManager assets)
{
    /// <summary>
    /// The most probes along x and z, and up y: the depth texture lays them out in a row of tiles along x and up, as
    /// many as its 16384 texels hold.
    /// </summary>
    private const int MaxProbesAcross = 48;
    private const int MaxProbesUp = 16;

    /// <summary>
    /// The rays all the probes trace in a frame, the twin of <c>UpdateProbes.slang</c>'s.
    /// </summary>
    private const int RayBudget = 131072;

    /// <summary>
    /// The twins of <c>Light/Probes.slang</c>'s.
    /// </summary>
    private const int IrradianceTexels = 8;
    private const int DepthTile = 18;
    private const int ProbeBytes = 80;

    private const int RayBytes = 16;
    private const int VarianceBytes = 16;

    /// <summary>
    /// The metres the grid reaches past the traced world's box on every side, and how far towards it the grid moves a
    /// frame once the probes hold light, so it doesn't shake their light as instances come and go.
    /// </summary>
    private const float BoxMargin = 1;
    private const float BoxBlend = 0.01f;

    private readonly ShaderEntry _askShader = PassShaders.GetEntry(assets, "UpdateProbes", "AskProbeRays");
    private readonly ShaderEntry _planShader = PassShaders.GetEntry(assets, "UpdateProbes", "PlanProbeRays");
    private readonly ShaderEntry _listShader = PassShaders.GetEntry(assets, "UpdateProbes", "ListProbeRays");
    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "UpdateProbes", "TraceProbes") with
    {
        TypeArguments = [GpuView.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "UpdateProbes", "BlendProbes");

    /// <summary>
    /// The box the grid spans, where the last frame placed it, and its probes along x and z, and up y.
    /// </summary>
    private (Vector3 Min, Vector3 Max) _box;

    private (int Across, int Up) _size;

    public ImmutableArray<ShaderEntry> Shaders => [_askShader, _planShader, _listShader, _traceShader, _blendShader];

    /// <summary>
    /// The grid's buffers and depth texture asked for at <paramref name="size"/>, from 2 probes a side up to the most,
    /// named by the grid that shading reads and by the update's own constants, and the probes started at none while
    /// they hold nothing or the size changed. The
    /// grid is placed over <paramref name="box"/> with a margin: at once when the probes start afresh, else a little
    /// nearer each frame.
    /// </summary>
    public Result<(ProbeGridConstants Grid, ProbeUpdateConstants Update)> Prepare(
        RenderGraph graph,
        bool hasHistory,
        (Vector3 Min, Vector3 Max) box,
        (int Across, int Up) size
    )
    {
        size = (Math.Clamp(size.Across, 2, MaxProbesAcross), Math.Clamp(size.Up, 2, MaxProbesUp));
        hasHistory &= size == _size;
        _size = size;
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
        _box = hasHistory
            ? (Vector3.Lerp(_box.Min, target.Min, BoxBlend), Vector3.Lerp(_box.Max, target.Max, BoxBlend))
            : target;
        Vector3 cellSize = (_box.Max - _box.Min) / new Vector3(across - 1, up - 1, across - 1);
        ProbeGridConstants grid = new()
        {
            DimensionX = (uint)across,
            DimensionY = (uint)up,
            DimensionZ = (uint)across,
            Probes = probeTable.Value,
            Depth = depth.Value,
            DepthSampler = sampler.Value,
            Origin = _box.Min,
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
    /// The rays the probes ask for given to them in turn while they fit in the frame's budget, the probe the next frame
    /// starts at kept in the trace's arguments; traced through the world, lit by the lights and the sky, and blended
    /// into the probes given them.
    /// </summary>
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
            .Compute("Trace probes", _traceShader, header, RenderStages.Light)
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
