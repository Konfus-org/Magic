using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Draws the depth of the clusters that cast into each shadow view into the views' atlas.
/// </summary>
/// <remarks>
/// The GPU culls each instance's clusters against each view and the view's texel, and lists each that passes with its
/// view. Then one indirect draw a pipeline draws the whole list into the atlas, each cluster into its view's
/// rectangle. The cull reads the impostor handoff from the camera's frame, and the pools from the cluster constants
/// that the view's passes write into it.
/// </remarks>
internal sealed class ShadowPasses
{
    /// <summary>
    /// The rows of the shadow list: 12 MB.
    /// </summary>
    private const int MaxShadowClusters = 1 << 20;

    /// <summary>
    /// The draws each region of the shadow draws holds.
    /// </summary>
    private const int MaxShadowDraws = 1 << 19;

    private const int ShadowClusterBytes = 12;

    private const int DrawArgumentsBytes = 16;

    private const int CullGroupsPerRow = 65535;

    private const BufferUsages IndirectUsage = BufferUsages.Storage | BufferUsages.Indirect;

    /// <summary>
    /// The counts buffer's words: a draw count for each region, then the rows of the shadow list.
    /// </summary>
    private const int CountWords = (int)ShadowDrawRegion.Impostors + 2;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _cullShader;

    private readonly DrawShaders _singleSidedShaders;

    private readonly DrawShaders _doubleSidedShaders;

    private readonly DrawShaders _fadingShaders;

    private readonly DrawShaders _fadingDoubleSidedShaders;

    private readonly DrawShaders _impostorShaders;

    internal ShadowPasses(AssetManager assets, IGraphicsApi gpu)
    {
        _gpu = gpu;
        _cullShader = PassShaders.GetEntry(assets, "CullShadowClusters", "CullShadowClusters");
        ShaderEntry vertex = PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowVertex", ShaderStage.Vertex);
        _singleSidedShaders = new(
            vertex,
            PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowPixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.TestAndWrite,
        };
        _doubleSidedShaders = _singleSidedShaders with { Cull = CullMode.None };
        _fadingShaders = _singleSidedShaders with
        {
            Pixel = PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowFadingPixel", ShaderStage.Pixel),
        };
        _fadingDoubleSidedShaders = _fadingShaders with { Cull = CullMode.None };
        _impostorShaders = new(
            PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowImpostorVertex", ShaderStage.Vertex),
            PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowImpostorPixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.TestAndWrite,
            Cull = CullMode.None,
        };
        Shaders =
        [
            _cullShader,
            vertex,
            _singleSidedShaders.Pixel,
            _fadingShaders.Pixel,
            _impostorShaders.Vertex,
            _impostorShaders.Pixel,
        ];
    }

    internal ImmutableArray<ShaderEntry> Shaders { get; }

    /// <summary>
    /// Culls and draws the clusters that cast into <paramref name="cascades"/> into their atlas, for the view of this
    /// turn.
    /// </summary>
    internal Result<CascadeDepth> Record(
        in RenderFrame frame,
        RenderGraph graph,
        GpuView view,
        ClusterPools pools,
        CascadeViews cascades
    )
    {
        Result<GpuTexture> atlas = _gpu.GetTexture(
            new(
                "Cascade atlas",
                cascades.AtlasSize,
                cascades.AtlasSize,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            ),
            frame.View
        );
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Cascade cull constants", Unsafe.SizeOf<ShadowCullConstants>()),
            frame.View
        );
        Result<GpuBuffer> counts = _gpu.GetBuffer(
            new("Cascade counts", CountWords * sizeof(uint), IndirectUsage),
            frame.View
        );
        Result<GpuBuffer> shadowClusters = _gpu.GetBuffer(
            new("Cascade clusters", (long)MaxShadowClusters * ShadowClusterBytes),
            frame.View
        );
        Result<GpuBuffer> drawArguments = _gpu.GetBuffer(
            new(
                "Cascade draw arguments",
                (long)MaxShadowDraws * ((int)ShadowDrawRegion.Impostors + 1) * DrawArgumentsBytes,
                IndirectUsage
            ),
            frame.View
        );
        Result asked = Result.FindFailure(atlas, constants, counts, shadowClusters, drawArguments);
        if (!asked.Ok)
            return Result.Failure(asked.Error);

        ShadowCullConstants cull = new()
        {
            Views = cascades.Views.Id,
            ViewCount = (uint)cascades.Count,
            ShadowClusters = shadowClusters.Value.Id,
            DrawArguments = drawArguments.Value.Id,
            Counts = counts.Value.Id,
            MaxShadowClusters = MaxShadowClusters,
            MaxShadowDraws = MaxShadowDraws,
        };
        Span<uint> zeros = stackalloc uint[CountWords];
        zeros.Clear();
        if (!graph.Upload(constants.Value, [cull]) || !graph.Upload(counts.Value, zeros))
            return Result.Failure("the cascades' cull constants were not staged.");

        // Cull: one group an instance, over every cascade.
        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        uint across = (uint)Math.Min(view.InstanceCount, CullGroupsPerRow);
        uint down = (uint)Math.Max((view.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow, 1);
        graph
            .Compute("Cull shadow clusters", _cullShader, header, RenderStages.Geometry)
            .Reads(
                view.Frame,
                constants.Value,
                cascades.Views,
                view.Instances,
                view.Materials,
                pools.Clusters,
                pools.Models
            )
            .Writes(counts.Value, shadowClusters.Value, drawArguments.Value)
            .Dispatch(across, down);

        // Draw: the first pass clears the whole atlas, each cluster draws into its cascade's rectangle.
        Draw("Draw shadow depth", _singleSidedShaders, TargetLoad.Clear, ShadowDrawRegion.SingleSided);
        Draw("Draw shadow depth double-sided", _doubleSidedShaders, TargetLoad.Keep, ShadowDrawRegion.DoubleSided);
        Draw("Draw shadow depth fading", _fadingShaders, TargetLoad.Keep, ShadowDrawRegion.Fading);
        Draw(
            "Draw shadow depth fading double-sided",
            _fadingDoubleSidedShaders,
            TargetLoad.Keep,
            ShadowDrawRegion.FadingDoubleSided
        );
        DrawIndirect(
            graph
                .Draw(
                    "Draw shadow impostors",
                    _impostorShaders,
                    GetTargets(TargetLoad.Keep),
                    header,
                    RenderStages.Geometry
                )
                .Reads(
                    view.Frame,
                    constants.Value,
                    cascades.Views,
                    shadowClusters.Value,
                    view.Instances,
                    pools.Impostors
                )
                .Reads(pools.Models, view.Materials)
                .Reads(pools.Atlases.AsSpan()),
            ShadowDrawRegion.Impostors
        );

        return Result.Success(new CascadeDepth(atlas.Value));

        GpuRenderTargets GetTargets(TargetLoad load) =>
            new()
            {
                Depth = new GpuTarget { Texture = atlas.Value, Load = load },
            };

        void Draw(string name, in DrawShaders shaders, TargetLoad load, ShadowDrawRegion region) =>
            DrawIndirect(
                graph
                    .Draw(name, shaders, GetTargets(load), header, RenderStages.Geometry)
                    .Reads(
                        view.Frame,
                        constants.Value,
                        cascades.Views,
                        shadowClusters.Value,
                        view.Instances,
                        view.Vertices,
                        pools.Vertices,
                        pools.Clusters,
                        pools.Models,
                        pools.Indices,
                        pools.Impostors
                    ),
                region
            );

        // One indirect draw of each that the cull left in the region, as many as it counted at the region's word.
        void DrawIndirect(DrawPass draw, ShadowDrawRegion region) =>
            draw.Indirect(drawArguments.Value, counts.Value)
                .DrawIndirectCount(
                    drawArguments.Value,
                    (ulong)region * MaxShadowDraws * DrawArgumentsBytes,
                    counts.Value,
                    (ulong)region * sizeof(uint),
                    MaxShadowDraws
                );
    }

    /// <summary>
    /// The regions of the shadow draws, the twin of <c>Scene/ShadowDraws.slang</c>'s: a pipeline each.
    /// </summary>
    private enum ShadowDrawRegion
    {
        SingleSided,
        DoubleSided,
        Fading,
        FadingDoubleSided,
        Impostors,
    }
}
