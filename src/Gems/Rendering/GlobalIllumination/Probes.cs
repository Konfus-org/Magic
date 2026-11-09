using System.Collections.Immutable;
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
/// still changes, traced, and blended into each probe's light and distances. The GPU places the box, following the
/// world's slowly, so the probes' light moves with it. What the probes hold carries from frame to frame, from the
/// first update on.
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

    private const int RayBytes = 16;
    private const int VarianceBytes = 16;

    private readonly ShaderEntry _placeShader = PassShaders.GetEntry(assets, "UpdateProbes", "PlaceProbes");
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

    public ImmutableArray<ShaderEntry> Shaders =>
        [_placeShader, _allocateShader, _prepareShader, _traceShader, _irradianceShader, _depthShader];

    /// <summary>
    /// The grid's buffers and depth texture asked for, the constants that name them, and the counts cleared, and the
    /// probes too while they hold nothing.
    /// </summary>
    public static Result<ProbeBuffers> Prepare(RenderGraph graph, bool hasHistory)
    {
        long probes = ProbeCount;
        Result<GpuBuffer> probeTable = graph.Buffer(SharedResources.Probes);
        Result<GpuBuffer> rays = graph.Buffer(GpuDescriptions.GetBuffer("Probe rays", probes * MaxRays * RayBytes));
        Result<GpuBuffer> list = graph.Buffer(GpuDescriptions.GetBuffer("Probe ray list", probes * MaxRays * 4));
        Result<GpuBuffer> arguments = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe ray arguments", 16, BufferUsages.Storage | BufferUsages.Indirect)
        );
        Result<GpuBuffer> counts = graph.Buffer(GpuDescriptions.GetBuffer("Probe ray counts", (probes + 1) * 4));
        Result<GpuBuffer> variance = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe variance", probes * IrradianceTexels * IrradianceTexels * VarianceBytes)
        );
        Result<GpuTexture> depth = graph.Texture(SharedResources.ProbeDepth);
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        foreach (
            Result made in (ReadOnlySpan<Result>)[probeTable, rays, list, arguments, counts, variance, depth, sampler]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

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
    /// The grid placed over the traced world, and the probes' rays allocated, traced through the world, lit by the
    /// lights and the sky, and blended into the probes.
    /// </summary>
    public void Record(in GIView view, in ProbeBuffers probes)
    {
        RenderGraph graph = view.Graph;
        GpuConstants header = view.Header;
        graph
            .Compute("Place probes", _placeShader, header, RenderStage.Light)
            .Reads(view.Frame)
            .Writes(view.IndirectLight)
            .Dispatch(1);
        graph
            .Compute("Allocate probe rays", _allocateShader, header, RenderStage.Light)
            .Reads(view.Frame, view.IndirectLight, probes.Probes, probes.Variance)
            .Writes(probes.RayCounts, probes.RayList)
            .Dispatch(ProbeCount);
        graph
            .Compute("Prepare probe rays", _prepareShader, header, RenderStage.Light)
            .Reads(view.Frame, view.IndirectLight, probes.RayCounts)
            .Writes(probes.RayArguments)
            .Dispatch(1);
        graph
            .Compute("Trace probes", _traceShader, header, RenderStage.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Frame, view.IndirectLight, probes.Probes, probes.RayList, probes.RayCounts, probes.Depth)
            .Writes(probes.Rays)
            .Indirect(probes.RayArguments)
            .DispatchIndirect(probes.RayArguments, 0);
        graph
            .Compute("Blend probe irradiance", _irradianceShader, header, RenderStage.Light)
            .Reads(view.Frame, view.IndirectLight, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Variance)
            .Dispatch(ProbeCount);
        graph
            .Compute("Blend probe depth", _depthShader, header, RenderStage.Light)
            .Reads(view.Frame, view.IndirectLight, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Depth)
            .Dispatch(ProbeCount);
    }
}
