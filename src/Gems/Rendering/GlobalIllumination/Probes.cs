using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// The probe grid's buffers and depth texture, and the constants that name them.
/// </summary>
internal readonly record struct ProbeBuffers(
    ProbeGridConstants Constants,
    GpuBuffer Probes,
    GpuBuffer Rays,
    GpuBuffer RayList,
    GpuBuffer RayArguments,
    GpuBuffer RayCounts,
    GpuBuffer Variance,
    GpuTexture Depth
);

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
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
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
    /// the constants that name them, and the probes started at none while they hold nothing or the size changed. The
    /// grid is placed over <paramref name="box"/> with a margin: at once when the probes start afresh, else a little
    /// nearer each frame.
    /// </summary>
    public Result<ProbeBuffers> Prepare(
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
        Result<GpuBuffer> probeTable = graph.Buffer(GpuDescriptions.GetBuffer("Probes", probes * ProbeBytes));
        Result<GpuBuffer> rays = graph.Buffer(GpuDescriptions.GetBuffer("Probe rays", RayBudget * RayBytes));
        Result<GpuBuffer> list = graph.Buffer(GpuDescriptions.GetBuffer("Probe ray list", RayBudget * 4));
        Result<GpuBuffer> arguments = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe ray arguments", 16, BufferUsages.Storage | BufferUsages.Indirect)
        );
        Result<GpuBuffer> counts = graph.Buffer(GpuDescriptions.GetBuffer("Probe ray counts", ((probes * 2) + 1) * 4));
        Result<GpuBuffer> variance = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe variance", probes * IrradianceTexels * IrradianceTexels * VarianceBytes)
        );
        Result<GpuTexture> depth = graph.Texture(
            GpuDescriptions.GetTexture(
                "Probe depth",
                DepthTile * across * up,
                DepthTile * across,
                GpuFormat.R16G16Sfloat
            )
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
            Probes = probeTable.Value.Id,
            Depth = depth.Value.Id,
            DepthSampler = sampler.Value.Id,
            Rays = rays.Value.Id,
            RayList = list.Value.Id,
            RayArguments = arguments.Value.Id,
            RayCounts = counts.Value.Id,
            Variance = variance.Value.Id,
            Origin = _box.Min,
            MaxDistance = MathF.Max(cellSize.X, MathF.Max(cellSize.Y, cellSize.Z)) * 1.5f,
            CellSize = cellSize,
        };
        ProbeBuffers buffers = new(
            grid,
            probeTable.Value,
            rays.Value,
            list.Value,
            arguments.Value,
            counts.Value,
            variance.Value,
            depth.Value
        );

        // The probes start at none on the first update.
        if (!hasHistory)
            graph.Clear(buffers.Probes);

        return Result.Success(buffers);
    }

    /// <summary>
    /// The rays the probes ask for given to them in turn while they fit in the frame's budget, the probe the next frame
    /// starts at kept in the trace's arguments; traced through the world, lit by the lights and the sky, and blended
    /// into the probes given them.
    /// </summary>
    public void Record(in GIView view, in ProbeBuffers probes)
    {
        RenderGraph graph = view.Graph;
        GpuConstants header = view.Header;
        ProbeGridConstants grid = probes.Constants;
        uint probeCount = grid.DimensionX * grid.DimensionY * grid.DimensionZ;
        graph
            .Compute("Ask probe rays", _askShader, header, RenderStage.Light)
            .Reads(view.Gpu.Frame, probes.Probes, probes.Variance)
            .Writes(probes.RayCounts)
            .Dispatch(probeCount);
        graph
            .Compute("Plan probe rays", _planShader, header, RenderStage.Light)
            .Reads(view.Gpu.Frame)
            .Writes(probes.RayCounts, probes.RayArguments)
            .Dispatch(1);
        graph
            .Compute("List probe rays", _listShader, header, RenderStage.Light)
            .Reads(view.Gpu.Frame, probes.RayCounts)
            .Writes(probes.RayList)
            .Dispatch(probeCount);
        graph
            .Compute("Trace probes", _traceShader, header, RenderStage.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, probes.Probes, probes.RayList, probes.RayCounts, probes.Depth)
            .Writes(probes.Rays)
            .Indirect(probes.RayArguments)
            .DispatchIndirect(probes.RayArguments, 0);
        graph
            .Compute("Blend probes", _blendShader, header, RenderStage.Light)
            .Reads(view.Gpu.Frame, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Variance, probes.Depth)
            .Dispatch(probeCount);
    }
}
