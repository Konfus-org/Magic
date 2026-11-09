using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

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
    GpuTexture Depth,
    GpuBuffer DrawArguments
);

/// <summary>
/// On a GPU that traces rays, the probe grid of diffuse light: a box of probes over every instance's bounds, each frame
/// given rays as their light still changes, traced, and blended into each probe's light and distances; and the debug
/// view that draws them. The box follows the instances' bounds slowly, so the probes' light moves with it. What the
/// probes hold carries from frame to frame, from the first update on.
/// </summary>
internal sealed class Probes(AssetManager assets, GpuResources resources)
{
    /// <summary>
    /// Probes along x and z, and up y.
    /// </summary>
    private const int ProbesAcross = 32;
    private const int ProbesUp = 8;
    private const int ProbeCount = ProbesAcross * ProbesUp * ProbesAcross;

    /// <summary>
    /// The twins of <c>Scene/Probes.slang</c>'s.
    /// </summary>
    private const int MaxRays = 256;
    private const int IrradianceTexels = 8;
    private const int DepthTile = 18;

    /// <summary>
    /// The twin of <c>DrawProbes.slang</c>'s sphere: 8 stacks of 16 quads, six vertices each.
    /// </summary>
    private const int SphereVertices = 8 * 16 * 6;
    private const int ProbeBytes = 80;
    private const int RayBytes = 16;
    private const int VarianceBytes = 16;
    private const float BoundsBlend = 0.01f;

    /// <summary>
    /// The metres the box reaches past the instances' bounds on every side.
    /// </summary>
    private const float BoundsMargin = 1;

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
    private readonly DrawShaders _drawShaders = new(
        PassShaders.GetEntry(assets, "DrawProbes", "DrawProbesVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawProbes", "DrawProbesPixel", ShaderStage.Pixel)
    )
    {
        Depth = DepthMode.TestAndWrite,
        Cull = CullMode.None,
    };

    /// <summary>
    /// The box over the instances' bounds the probes stand in, as it moves towards where they are; none while the probes
    /// hold nothing, so the next update starts them afresh.
    /// </summary>
    private (Vector3 Min, Vector3 Max)? _box;

    public ImmutableArray<ShaderEntry> Shaders =>
        [
            _allocateShader,
            _prepareShader,
            _traceShader,
            _irradianceShader,
            _depthShader,
            _drawShaders.Vertex,
            _drawShaders.Pixel,
        ];

    /// <summary>
    /// Forgets what the probes hold, so the next update starts them afresh where the world is then.
    /// </summary>
    public void ClearHistory() => _box = null;

    /// <summary>
    /// The grid's buffers and depth texture asked for, the box moved towards the boxes of the instances in the world's
    /// acceleration structure, the constants that name them, and the draw arguments and counts set, in a transfer pass
    /// the caller opened.
    /// </summary>
    public Result<ProbeBuffers> Prepare(in RenderView view, ReadOnlySpan<InstanceExtra> instances)
    {
        RenderGraph graph = view.Graph;
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
        Result<GpuBuffer> draw = graph.Buffer(
            GpuDescriptions.GetBuffer("Probe draw", 20, BufferUsages.Storage | BufferUsages.Indirect)
        );
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [probeTable, rays, list, arguments, counts, variance, depth, draw, sampler]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        // The box over the traced instances' boxes with a margin, followed slowly once the probes hold light, so it
        // doesn't shake their light as instances come and go.
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (InstanceExtra instance in instances)
        {
            if (instance.Acceleration == 0)
                continue;

            min = Vector3.Min(min, instance.Box.Min - new Vector3(BoundsMargin));
            max = Vector3.Max(max, instance.Box.Max + new Vector3(BoundsMargin));
        }

        (Vector3 Min, Vector3 Max) box = _box is { } last
            ? (Vector3.Lerp(last.Min, min, BoundsBlend), Vector3.Lerp(last.Max, max, BoundsBlend))
            : (min, max);
        bool hasHistory = _box is not null;
        _box = box;
        Vector3 cellSize = (box.Max - box.Min) / new Vector3(ProbesAcross - 1, ProbesUp - 1, ProbesAcross - 1);
        ProbeGridConstants grid = new()
        {
            DimensionX = ProbesAcross,
            DimensionY = ProbesUp,
            DimensionZ = ProbesAcross,
            MaxDistance = Math.Max(cellSize.X, Math.Max(cellSize.Y, cellSize.Z)) * 1.5f,
            Origin = box.Min,
            CellSize = cellSize,
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
            depth.Value,
            draw.Value
        );
        graph.Upload(buffers.DrawArguments, [(uint)SphereVertices, (uint)ProbeCount, 0u, 0u, 1u]);

        // The counts start each frame at none, and the probes at none on the first update.
        graph.Clear(buffers.RayCounts);
        if (!hasHistory)
            graph.Clear(buffers.Probes);

        return Result.Success(buffers);
    }

    /// <summary>
    /// The probes' rays allocated, traced through the world, lit by the lights and the sky, and blended into the probes.
    /// </summary>
    public void Record(
        in RenderView view,
        GpuBuffer hitMaterials,
        in SkyTables sky,
        in LightBuffers lights,
        in ProbeBuffers probes
    )
    {
        RenderGraph graph = view.Graph;
        GpuConstants header = new(view.Constants.Id);
        graph
            .Compute("Allocate probe rays", _allocateShader, header)
            .Reads(view.Constants, probes.Probes, probes.Variance)
            .Writes(probes.RayCounts, probes.RayList)
            .Dispatch(ProbeCount);
        graph
            .Compute("Prepare probe rays", _prepareShader, header)
            .Reads(view.Constants, probes.RayCounts)
            .Writes(probes.RayArguments)
            .Dispatch(1);
        graph
            .Compute("Trace probes", _traceShader, header)
            .Reads(
                view.Constants,
                sky.Transmittance,
                sky.MultiScattering,
                sky.View,
                lights.Table,
                view.Instances,
                resources.VertexPool,
                resources.IndexPool,
                resources.Meshes,
                hitMaterials,
                probes.Probes,
                probes.RayList,
                probes.RayCounts,
                probes.Depth
            )
            .Writes(probes.Rays)
            .Indirect(probes.RayArguments)
            .DispatchIndirect(probes.RayArguments, 0);
        graph
            .Compute("Blend probe irradiance", _irradianceShader, header)
            .Reads(view.Constants, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Variance)
            .Dispatch(ProbeCount);
        graph
            .Compute("Blend probe depth", _depthShader, header)
            .Reads(view.Constants, probes.Rays, probes.RayCounts)
            .Writes(probes.Probes, probes.Depth)
            .Dispatch(ProbeCount);
    }

    /// <summary>
    /// Each probe drawn into the picture as a sphere lit by the light it holds, behind what is nearer, before the post
    /// effects, which expose and tonemap it with the picture.
    /// </summary>
    public void RecordDraw(in RenderView view, in ProbeBuffers probes) =>
        view
            .Graph.Draw(
                "Draw probes",
                _drawShaders,
                new GpuRenderTargets
                {
                    Colors = [new GpuTarget { Texture = view.Hdr }],
                    Depth = new GpuTarget { Texture = view.Depth },
                },
                new GpuConstants(view.Constants.Id)
            )
            .Reads(view.Constants, probes.Probes)
            .Indirect(probes.DrawArguments)
            .DrawIndirectCount(probes.DrawArguments, 0, probes.DrawArguments, 16, 1);
}
