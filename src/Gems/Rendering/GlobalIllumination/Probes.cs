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
/// The probe grid of diffuse light: a box of probes over the traced world's box, each frame given rays as their light
/// still changes, traced, and blended into each probe's light and distances. The box follows the world's slowly, so the
/// probes' light moves with it. What the probes hold carries from frame to frame, from the first update on.
/// </summary>
internal sealed class Probes(AssetManager assets)
{
    /// <summary>
    /// Probes along x and z, and up y.
    /// </summary>
    private const int ProbesAcross = 32;
    private const int ProbesUp = 8;
    private const int ProbeCount = ProbesAcross * ProbesUp * ProbesAcross;

    /// <summary>
    /// The twins of <c>Light/Probes.slang</c>'s.
    /// </summary>
    private const int MaxRays = 256;
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

    private readonly ShaderEntry _allocateShader = PassShaders.GetEntry(assets, "UpdateProbes", "AllocateProbeRays");
    private readonly ShaderEntry _prepareShader = PassShaders.GetEntry(assets, "UpdateProbes", "PrepareProbeRays");
    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "UpdateProbes", "TraceProbes") with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _irradianceShader = PassShaders.GetEntry(
        assets,
        "UpdateProbes",
        "BlendProbeIrradiance"
    );
    private readonly ShaderEntry _depthShader = PassShaders.GetEntry(assets, "UpdateProbes", "BlendProbeDepth");

    /// <summary>
    /// The box the grid spans, where the last frame placed it.
    /// </summary>
    private (Vector3 Min, Vector3 Max) _box;

    public ImmutableArray<ShaderEntry> Shaders =>
        [_allocateShader, _prepareShader, _traceShader, _irradianceShader, _depthShader];

    /// <summary>
    /// The grid's buffers and depth texture asked for, the constants that name them, and the counts cleared, and the
    /// probes too while they hold nothing. The grid is placed over <paramref name="box"/> with a margin: at once when
    /// the probes start afresh, else a little nearer each frame.
    /// </summary>
    public Result<ProbeBuffers> Prepare(RenderGraph graph, bool hasHistory, (Vector3 Min, Vector3 Max) box)
    {
        long probes = ProbeCount;
        Result<GpuBuffer> probeTable = graph.Buffer(GpuDescriptions.GetBuffer("Probes", probes * ProbeBytes));
        Result<GpuBuffer> rays = graph.Buffer(GpuDescriptions.GetBuffer("Probe rays", probes * MaxRays * RayBytes));
        Result<GpuBuffer> list = graph.Buffer(GpuDescriptions.GetBuffer("Probe ray list", probes * MaxRays * 4));
        Result<GpuBuffer> arguments = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe ray arguments", 16, BufferUsages.Storage | BufferUsages.Indirect)
        );
        Result<GpuBuffer> counts = graph.Buffer(GpuDescriptions.GetBuffer("Probe ray counts", (probes + 1) * 4));
        Result<GpuBuffer> variance = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe variance", probes * IrradianceTexels * IrradianceTexels * VarianceBytes)
        );
        Result<GpuTexture> depth = graph.Texture(
            GpuDescriptions.GetTexture(
                "Probe depth",
                DepthTile * ProbesAcross * ProbesUp,
                DepthTile * ProbesAcross,
                GpuFormat.R16G16Sfloat
            )
        );
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        foreach (
            Result made in (ReadOnlySpan<Result>)[probeTable, rays, list, arguments, counts, variance, depth, sampler]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        (Vector3 Min, Vector3 Max) target = (box.Min - new Vector3(BoxMargin), box.Max + new Vector3(BoxMargin));
        _box = hasHistory
            ? (Vector3.Lerp(_box.Min, target.Min, BoxBlend), Vector3.Lerp(_box.Max, target.Max, BoxBlend))
            : target;
        Vector3 cellSize = (_box.Max - _box.Min) / new Vector3(ProbesAcross - 1, ProbesUp - 1, ProbesAcross - 1);
        ProbeGridConstants grid = new()
        {
            DimensionX = ProbesAcross,
            DimensionY = ProbesUp,
            DimensionZ = ProbesAcross,
            Probes = probeTable.Value.Id,
            Depth = depth.Value.Id,
            DepthSampler = sampler.Value.Id,
            Rays = rays.Value.Id,
            RayList = list.Value.Id,
            RayArguments = arguments.Value.Id,
            RayCounts = counts.Value.Id,
            Variance = variance.Value.Id,
            HasHistory = hasHistory ? 1u : 0u,
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

        // The counts start each frame at none, and the probes at none on the first update.
        graph.Clear(buffers.RayCounts);
        if (!hasHistory)
            graph.Clear(buffers.Probes);

        return Result.Success(buffers);
    }

    /// <summary>
    /// The probes' rays allocated, traced through the world, lit by the lights and the sky, and blended into the
    /// probes.
    /// </summary>
    public void Record(in GIView view, in ProbeBuffers probes)
    {
        RenderGraph graph = view.Graph;
        GpuConstants header = view.Header;
        graph
            .Compute("Allocate probe rays", _allocateShader, header, RenderStage.Light)
            .Reads(view.Frame, probes.Probes, probes.Variance)
            .Writes(probes.RayCounts, probes.RayList)
            .Dispatch(ProbeCount);
        graph
            .Compute("Prepare probe rays", _prepareShader, header, RenderStage.Light)
            .Reads(view.Frame, probes.RayCounts)
            .Writes(probes.RayArguments)
            .Dispatch(1);
        graph
            .Compute("Trace probes", _traceShader, header, RenderStage.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Frame, probes.Probes, probes.RayList, probes.RayCounts, probes.Depth)
            .Writes(probes.Rays)
            .Indirect(probes.RayArguments)
            .DispatchIndirect(probes.RayArguments, 0);
        graph
            .Compute("Blend probe irradiance", _irradianceShader, header, RenderStage.Light)
            .Reads(view.Frame, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Variance)
            .Dispatch(ProbeCount);
        graph
            .Compute("Blend probe depth", _depthShader, header, RenderStage.Light)
            .Reads(view.Frame, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Depth)
            .Dispatch(ProbeCount);
    }
}
