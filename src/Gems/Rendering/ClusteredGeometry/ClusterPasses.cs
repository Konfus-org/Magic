using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// What a view sees: the cluster budget's handoff and error threshold set from what last frame asked for; every
/// instance's clusters culled on the GPU against the frustum, their cones, the pixel error threshold and last frame's
/// depth pyramid; the visible ones drawn into a visibility buffer; what the old pyramid hid retested against the new
/// one; and every pixel's material resolved into the GBuffer.
/// </summary>
internal sealed class ClusterPasses
{
    public const int MaxVisibleClusters = 1 << 18;

    private const float ErrorThresholdPixels = 1;

    private const int BudgetBytes = 16;

    private const int CountsBytes = 320;

    private const int DrawArgumentsBytes = 16;

    private const int DrawRegions = (int)DrawRegion.RetestedImpostors + 1;

    private const int RetestArgumentsBytes = 12;

    private const int CullGroupsPerRow = 65535;

    private const BufferUsages IndirectUsage = BufferUsages.Storage | BufferUsages.Indirect;

    // A name for each level of the depth pyramid, as many as a texture can have: GPU timings are kept by name.
    private static readonly ImmutableArray<string> DepthPyramidPasses =
    [
        .. Enumerable.Range(0, 32).Select(level => $"Build depth pyramid {level}"),
    ];

    private readonly ShaderEntry _budgetShader;

    private readonly ShaderEntry _countShader;

    private readonly ShaderEntry _fitShader;

    private readonly ShaderEntry _placeShader;

    private readonly ShaderEntry _retestShader;

    private readonly DrawShaders _drawShaders;

    private readonly DrawShaders _fadingShaders;

    private readonly DrawShaders _glassShaders;

    private readonly DrawShaders _impostorShaders;

    private readonly ShaderEntry _depthPyramidShader;

    private readonly ShaderEntry _resolveShader;

    /// <summary>
    /// The resolve entry specialised to each of the resources' material shaders, by the number rows name it by: kept, so
    /// a frame makes none.
    /// </summary>
    private readonly List<ShaderEntry> _resolveShaders = [];

    public ClusterPasses(AssetManager assets)
    {
        _budgetShader = PassShaders.GetEntry(assets, "BudgetClusters", "BudgetClusters");
        _countShader = PassShaders.GetEntry(assets, "CullClusters", "CountClusters");
        _fitShader = PassShaders.GetEntry(assets, "CullClusters", "FitClusters");
        _placeShader = PassShaders.GetEntry(assets, "CullClusters", "PlaceClusters");
        _retestShader = PassShaders.GetEntry(assets, "CullClusters", "RetestClusters");
        ShaderEntry clusterVertex = PassShaders.GetEntry(
            assets,
            "DrawVisibility",
            "DrawVisibilityVertex",
            ShaderStage.Vertex
        );
        _drawShaders = new(
            clusterVertex,
            PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityPixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.TestAndWrite,
        };
        // A cluster of an instance in the impostor handoff, kept by the dither.
        _fadingShaders = _drawShaders with
        {
            Pixel = PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityFadingPixel", ShaderStage.Pixel),
        };
        // A translucent cluster, both faces, into the glass's buffer and depth where nothing opaque is nearer.
        _glassShaders = _drawShaders with
        {
            Pixel = PassShaders.GetEntry(assets, "DrawVisibility", "DrawGlassPixel", ShaderStage.Pixel),
            Cull = CullMode.None,
        };
        // An impostor's quad, both faces, its pixels writing their own depth no nearer than the quad's.
        _impostorShaders = new(
            PassShaders.GetEntry(assets, "DrawImpostors", "DrawImpostorsVertex", ShaderStage.Vertex),
            PassShaders.GetEntry(assets, "DrawImpostors", "DrawImpostorsPixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.TestAndWrite,
            Cull = CullMode.None,
        };
        _depthPyramidShader = PassShaders.GetEntry(assets, "BuildDepthPyramid", "BuildDepthPyramid");
        _resolveShader = PassShaders.GetEntry(assets, "ResolveMaterial", "ResolveMaterial");
        Shaders =
        [
            _budgetShader,
            _countShader,
            _fitShader,
            _placeShader,
            _retestShader,
            clusterVertex,
            _drawShaders.Pixel,
            _fadingShaders.Pixel,
            _glassShaders.Pixel,
            _impostorShaders.Vertex,
            _impostorShaders.Pixel,
            _depthPyramidShader,
        ];
    }

    public ImmutableArray<ShaderEntry> Shaders { get; }

    /// <summary>
    /// Asks for the cull's buffers and the cluster targets, uploads the cluster constants that name them and
    /// <paramref name="pools"/> after the air's row, then records the passes: the cluster budget set, the main view
    /// drawn twice, what last frame's pyramid shows, then what it hid and this frame's shows, the nearest glass in
    /// front of it drawn into the glass's buffer, and every pixel's material resolved into the GBuffer.
    /// </summary>
    public Result Record(RenderGraph graph, GpuView gpu, ClusterPools pools, ClusteredGeometrySettings settings)
    {
        (int width, int height) = (gpu.Width, gpu.Height);
        int depthPyramidLevels = TextureDescription.GetFullLevels(width, height);
        Result<GpuBuffer> budget = graph.Buffer(GpuDescriptions.GetBuffer("Cluster budget", BudgetBytes));
        Result<GpuBuffer> counts = graph.Buffer(GpuDescriptions.GetBuffer("Counts", CountsBytes, IndirectUsage));
        Result<GpuBuffer> instanceRows = graph.Buffer(
            GpuDescriptions.GetBuffer("Instance rows", (long)gpu.InstanceCapacity * sizeof(uint))
        );
        Result<GpuBuffer> retestArguments = graph.Buffer(
            GpuDescriptions.GetBuffer("Retest arguments", RetestArgumentsBytes, IndirectUsage)
        );
        Result<GpuBuffer> visibleClusters = graph.Buffer(
            GpuDescriptions.GetBuffer("Visible clusters", (long)MaxVisibleClusters * 8)
        );
        Result<GpuBuffer> drawArguments = graph.Buffer(
            GpuDescriptions.GetBuffer(
                "Draw arguments",
                (long)MaxVisibleClusters * DrawRegions * DrawArgumentsBytes,
                IndirectUsage
            )
        );
        Result<GpuBuffer> retestList = graph.Buffer(
            GpuDescriptions.GetBuffer("Retest list", (long)MaxVisibleClusters * 8)
        );
        Result<GpuTexture> visibility = graph.Texture(
            GpuDescriptions.GetTexture(
                "Visibility",
                width,
                height,
                GpuFormat.R32Uint,
                TextureUsages.ColorTarget | TextureUsages.Sampled
            )
        );
        // The glass's buffer only while the view has glass targets to resolve it into.
        Result<GpuTexture> glassVisibility = gpu.GlassDepth.IsNone
            ? Result.Success(GpuTexture.None)
            : graph.Texture(
                GpuDescriptions.GetTexture(
                    "Glass visibility",
                    width,
                    height,
                    GpuFormat.R32Uint,
                    TextureUsages.ColorTarget | TextureUsages.Sampled
                )
            );
        Result<GpuTexture> depthPyramid = graph.Texture(
            GpuDescriptions.GetTexture("Depth pyramid", width, height, GpuFormat.R32Sfloat, levels: depthPyramidLevels)
        );
        Result asked = Result.FindFailure(
            visibility,
            glassVisibility,
            depthPyramid,
            budget,
            counts,
            instanceRows,
            retestArguments,
            visibleClusters,
            drawArguments,
            retestList
        );
        if (!asked.Ok)
            return asked;

        // The retest dispatch at no groups, until the cull adds to it.
        graph.Upload(retestArguments.Value, [0u, 1u, 1u]);
        ClusterConstants clusters = new()
        {
            DepthPyramidLevels = (uint)depthPyramidLevels,
            HasDepthPyramid = gpu.HasHistory ? 1u : 0u,
            ErrorThreshold = ErrorThresholdPixels,
            ImpostorPixels = settings.ImpostorPixels,
            ImpostorDistance = settings.ImpostorDistance,
            MaxVisibleClusters = MaxVisibleClusters,
            Clusters = pools.Clusters.Id,
            Indices = pools.Indices.Id,
            Vertices = pools.Vertices.Id,
            Models = pools.Models.Id,
            VisibleClusters = visibleClusters.Value.Id,
            DrawArguments = drawArguments.Value.Id,
            Counts = counts.Value.Id,
            RetestList = retestList.Value.Id,
            RetestArguments = retestArguments.Value.Id,
            DepthPyramid = depthPyramid.Value.Id,
            Visibility = visibility.Value.Id,
            GlassVisibility = glassVisibility.Value.Id,
            Impostors = pools.Impostors.Id,
            InstanceRows = instanceRows.Value.Id,
            Budget = budget.Value.Id,
        };
        graph.Upload(gpu.Frame, [clusters], ClusterConstants.Offset);

        // Budget: last frame's count steers the frame's handoff and error threshold, then the counts start again.
        graph
            .Compute("Budget clusters", _budgetShader, new GpuConstants(gpu.Frame.Id), RenderStage.Geometry)
            .Writes(gpu.Frame, counts.Value, budget.Value)
            .Dispatch(1, 1);

        // Cull: one group an instance over its clusters, counted by distance, the nearest given their rows while the
        // list has room and the rest drawn as their impostors, what last frame's pyramid hid onto the retest list.
        uint across = (uint)Math.Min(gpu.InstanceCount, CullGroupsPerRow);
        uint down = (uint)Math.Max((gpu.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow, 1);
        graph
            .Compute("Count clusters", _countShader, new GpuConstants(gpu.Frame.Id), RenderStage.Geometry)
            .Reads(gpu.Frame, gpu.Instances, pools.Clusters, pools.Models, depthPyramid.Value)
            .Writes(counts.Value, instanceRows.Value)
            .Dispatch(across, down);
        graph
            .Compute("Fit clusters", _fitShader, new GpuConstants(gpu.Frame.Id), RenderStage.Geometry)
            .Reads(gpu.Frame)
            .Writes(counts.Value)
            .Dispatch(1, 1);
        graph
            .Compute("Place clusters", _placeShader, new GpuConstants(gpu.Frame.Id), RenderStage.Geometry)
            .Reads(
                gpu.Frame,
                gpu.Instances,
                gpu.Materials,
                pools.Clusters,
                pools.Models,
                depthPyramid.Value,
                instanceRows.Value
            )
            .Writes(counts.Value, visibleClusters.Value, drawArguments.Value, retestList.Value, retestArguments.Value)
            .Dispatch(across, down);
        Draw("Draw visibility", _drawShaders, GetTargets(TargetLoad.Clear), DrawRegion.Visible);
        Draw("Draw fading", _fadingShaders, GetTargets(TargetLoad.Keep), DrawRegion.Fading);
        DrawImpostors("Draw impostors", DrawRegion.Impostors);
        BuildDepthPyramid(GpuTexture.None);

        // Retest the list against the new pyramid, as many groups as the cull left in the retest arguments.
        graph
            .Compute("Retest clusters", _retestShader, new GpuConstants(gpu.Frame.Id), RenderStage.Geometry)
            .Reads(
                gpu.Frame,
                gpu.Instances,
                gpu.Materials,
                pools.Clusters,
                pools.Models,
                depthPyramid.Value,
                retestList.Value,
                visibleClusters.Value
            )
            .Writes(counts.Value, drawArguments.Value)
            .Indirect(retestArguments.Value)
            .DispatchIndirect(retestArguments.Value, 0);
        Draw("Draw retested", _drawShaders, GetTargets(TargetLoad.Keep), DrawRegion.Retested);
        Draw("Draw retested fading", _fadingShaders, GetTargets(TargetLoad.Keep), DrawRegion.RetestedFading);
        DrawImpostors("Draw retested impostors", DrawRegion.RetestedImpostors);

        // The picture cleared before the material resolve writes emissive light into it, so a pixel that no resolve
        // writes this frame, while its pass builds or after it failed, adds none.
        BuildDepthPyramid(gpu.Hdr);

        // The nearest translucent surface in front of the opaque ones, from both passes' draws, where the view has glass.
        if (!glassVisibility.Value.IsNone)
        {
            Draw(
                "Draw glass",
                _glassShaders,
                new GpuRenderTargets
                {
                    Colors = [new GpuTarget { Texture = glassVisibility.Value, Load = TargetLoad.Clear }],
                    Depth = new GpuTarget { Texture = gpu.GlassDepth, Load = TargetLoad.Clear },
                },
                DrawRegion.Glass,
                gpu.Depth
            );
        }

        // Resolve each pixel's material into the GBuffer, with its emissive light in the picture for the shading to add
        // to, a pass for each material shader, which resolves the pixels whose rows it draws, and the panes it draws into
        // the glass targets where the view has them.
        ReadOnlySpan<ShaderEntry> resolves = GetResolveShaders(gpu.MaterialShaders);
        for (int shader = 0; shader < resolves.Length; shader++)
        {
            graph
                .Compute(
                    "Resolve materials",
                    resolves[shader],
                    new GpuConstants(gpu.Frame.Id, (uint)shader),
                    RenderStage.Geometry
                )
                .Reads(
                    gpu.Frame,
                    gpu.Instances,
                    pools.Impostors,
                    pools.Clusters,
                    pools.Models,
                    gpu.Vertices,
                    pools.Vertices,
                    pools.Indices,
                    gpu.Materials,
                    visibleClusters.Value,
                    visibility.Value,
                    glassVisibility.Value
                )
                .Reads(gpu.HeapTextures.AsSpan())
                .Reads(pools.Atlases.AsSpan())
                .Writes(gpu.GBufferNormal, gpu.GBufferBase, gpu.GBufferSurface, gpu.Velocity, gpu.Hdr)
                .Writes(gpu.GlassBase, gpu.GlassNormal, gpu.GlassSurface)
                .DispatchOver(gpu.Width, gpu.Height);
        }

        return Result.Success();

        // The view's visibility buffer and depth, kept or cleared.
        GpuRenderTargets GetTargets(TargetLoad load) =>
            new()
            {
                Colors = [new GpuTarget { Texture = visibility.Value, Load = load }],
                Depth = new GpuTarget { Texture = gpu.Depth, Load = load },
            };

        // The visible clusters of a region of the draws, the shaders reading the reads given beside the clusters.
        void Draw(
            string name,
            in DrawShaders shaders,
            in GpuRenderTargets targets,
            DrawRegion region,
            params ReadOnlySpan<GpuResource> reads
        ) =>
            DrawIndirect(
                graph
                    .Draw(name, shaders, targets, new GpuConstants(gpu.Frame.Id), RenderStage.Geometry)
                    .Reads(
                        gpu.Frame,
                        gpu.Instances,
                        gpu.Vertices,
                        pools.Vertices,
                        pools.Clusters,
                        pools.Models,
                        pools.Indices,
                        visibleClusters.Value
                    )
                    .Reads(reads),
                region
            );

        // The impostors of a region of the draws, into the visibility buffer and depth, reading their atlases.
        void DrawImpostors(string name, DrawRegion region) =>
            DrawIndirect(
                graph
                    .Draw(
                        name,
                        _impostorShaders,
                        GetTargets(TargetLoad.Keep),
                        new GpuConstants(gpu.Frame.Id),
                        RenderStage.Geometry
                    )
                    .Reads(gpu.Frame, gpu.Instances, pools.Impostors, pools.Models)
                    .Reads(gpu.HeapTextures.AsSpan())
                    .Reads(pools.Atlases.AsSpan()),
                region
            );

        // One indirect draw of each the cull left in the region, as many as it counted at the region's word of the
        // counts.
        void DrawIndirect(DrawPass draw, DrawRegion region) =>
            draw.Indirect(drawArguments.Value, counts.Value)
                .DrawIndirectCount(
                    drawArguments.Value,
                    (ulong)region * MaxVisibleClusters * DrawArgumentsBytes,
                    counts.Value,
                    (ulong)region * sizeof(uint),
                    MaxVisibleClusters
                );

        // The depth pyramid from the depth so far, a level a pass, the first also clearing the picture unless it is
        // none.
        void BuildDepthPyramid(GpuTexture picture)
        {
            for (int level = 0; level < depthPyramidLevels; level++)
            {
                ComputePass build = graph
                    .Compute(
                        DepthPyramidPasses[level],
                        _depthPyramidShader,
                        new GpuConstants(gpu.Frame.Id, (uint)level, picture.Id),
                        RenderStage.Geometry
                    )
                    .Reads(gpu.Frame);
                if (level == 0)
                    build.Reads(gpu.Depth).Writes(picture);
                else
                    build.Reads(depthPyramid.Value, level - 1);

                build.Writes(depthPyramid.Value, level);
                build.DispatchOver(Math.Max(width >> level, 1), Math.Max(height >> level, 1));
            }
        }
    }

    /// <summary>
    /// The resolve entry for each of <paramref name="materialShaders"/>, one made for each newly numbered.
    /// </summary>
    public ReadOnlySpan<ShaderEntry> GetResolveShaders(IReadOnlyList<ShaderTypeArgument> materialShaders)
    {
        for (int shader = _resolveShaders.Count; shader < materialShaders.Count; shader++)
        {
            _resolveShaders.Add(_resolveShader with { TypeArguments = [materialShaders[shader]] });
        }

        return CollectionsMarshal.AsSpan(_resolveShaders);
    }

    /// <summary>
    /// The regions of the draw arguments, each as many draws as the visible list has rows and counted at word
    /// <c>region * 4</c> of the counts, the twin of <c>Scene/Visibility.slang</c>'s: the clusters last frame's depth
    /// pyramid shows and those the retest finds this frame's shows, the same of the instances in the impostor handoff,
    /// the translucent clusters of both, and the impostors of both.
    /// </summary>
    private enum DrawRegion
    {
        Visible,
        Retested,
        Fading,
        RetestedFading,
        Glass,
        Impostors,
        RetestedImpostors,
    }
}
