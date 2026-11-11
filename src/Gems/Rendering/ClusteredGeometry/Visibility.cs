using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// The view as the world on the GPU posted it this frame, the graph its passes record into, the pools the clusters are
/// in, and the targets and buffers they are drawn through, all the view's size. The cull's buffers: what each pass of
/// it counts, with the rows asked for by distance, the rows each instance asks for, the visible clusters and their
/// draws, what last frame's depth pyramid hid, to retest, and the cluster budget's level, which carries from frame to
/// frame.
/// </summary>
internal readonly record struct RenderView(
    GpuView Gpu,
    RenderGraph Graph,
    ClusterPools Pools,
    GpuBuffer Budget,
    GpuBuffer Counts,
    GpuBuffer InstanceRows,
    GpuBuffer RetestArguments,
    GpuBuffer VisibleClusters,
    GpuBuffer DrawArguments,
    GpuBuffer RetestList,
    GpuTexture Visibility,
    GpuTexture GlassVisibility,
    GpuTexture Hzb
)
{
    public int HzbLevels => TextureDescription.GetFullLevels(Gpu.Width, Gpu.Height);
}

/// <summary>
/// What a view sees: the cluster budget's handoff and error threshold set from what last frame asked for; every
/// instance's clusters culled on the GPU against the frustum, their cones, the pixel error threshold and last frame's
/// depth pyramid; the visible ones drawn into a visibility buffer; what the old pyramid hid retested against the new
/// one; and every pixel's material resolved into the GBuffer.
/// </summary>
internal sealed class Visibility(AssetManager assets)
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

    // The cull's passes, the pass header's y: count each instance's rows by distance, find how many distances from the
    // nearest fit, give those their rows, and retest what last frame's pyramid hid.
    private const uint ModeCount = 0;

    private const uint ModeFit = 1;

    private const uint ModePlace = 2;

    private const uint ModeRetest = 3;

    private readonly ShaderEntry _budgetShader = PassShaders.GetEntry(assets, "BudgetClusters", "BudgetClusters");

    private readonly ShaderEntry _cullShader = PassShaders.GetEntry(assets, "CullClusters", "CullClusters");

    private readonly DrawShaders _drawShaders = new(
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityPixel", ShaderStage.Pixel)
    )
    {
        Depth = DepthMode.TestAndWrite,
    };

    // A cluster of an instance in the impostor handoff, kept by the dither.
    private readonly DrawShaders _fadingShaders = new(
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityFadingPixel", ShaderStage.Pixel)
    )
    {
        Depth = DepthMode.TestAndWrite,
    };

    // A translucent cluster, both faces, into the glass's buffer and depth where nothing opaque is nearer.
    private readonly DrawShaders _glassShaders = new(
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawGlassPixel", ShaderStage.Pixel)
    )
    {
        Depth = DepthMode.TestAndWrite,
        Cull = CullMode.None,
    };

    // An impostor's quad, both faces, its pixels writing their own depth no nearer than the quad's.
    private readonly DrawShaders _impostorShaders = new(
        PassShaders.GetEntry(assets, "DrawImpostors", "DrawImpostorsVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawImpostors", "DrawImpostorsPixel", ShaderStage.Pixel)
    )
    {
        Depth = DepthMode.TestAndWrite,
        Cull = CullMode.None,
    };

    private readonly ShaderEntry _buildHzbShader = PassShaders.GetEntry(assets, "BuildHzb", "BuildHzb");

    private readonly ShaderEntry _resolveShader = PassShaders.GetEntry(assets, "ResolveMaterial", "ResolveMaterial");

    /// <summary>
    /// The resolve entry specialised to each of the resources' material shaders, by the number rows name it by: kept, so
    /// a frame makes none.
    /// </summary>
    private readonly List<ShaderEntry> _resolveShaders = [];

    public ImmutableArray<ShaderEntry> Shaders =>
        [
            _budgetShader,
            _cullShader,
            _drawShaders.Vertex,
            _drawShaders.Pixel,
            _fadingShaders.Pixel,
            _glassShaders.Pixel,
            _impostorShaders.Vertex,
            _impostorShaders.Pixel,
            _buildHzbShader,
        ];

    /// <summary>
    /// The view the clusters are drawn into: the cull's buffers and the cluster targets asked for, the retest dispatch at
    /// no groups, and the cluster constants that name them and <paramref name="pools"/> uploaded after the air's
    /// row. The budget pass zeroes the counts once it has read last frame's.
    /// </summary>
    public static Result<RenderView> Prepare(
        RenderGraph graph,
        in GpuView gpu,
        in ClusterPools pools,
        float impostorPixels,
        float impostorDistance
    )
    {
        (int width, int height) = (gpu.Width, gpu.Height);
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
        Result<GpuTexture> hzb = graph.Texture(
            GpuDescriptions.GetTexture(
                "Depth pyramid",
                width,
                height,
                GpuFormat.R32Sfloat,
                levels: TextureDescription.GetFullLevels(width, height)
            )
        );
        Result asked = Result.FindFailure(
            visibility,
            glassVisibility,
            hzb,
            budget,
            counts,
            instanceRows,
            retestArguments,
            visibleClusters,
            drawArguments,
            retestList
        );
        if (!asked.Ok)
            return Result.Failure(asked.Error);

        graph.Upload(retestArguments.Value, [0u, 1u, 1u]);
        RenderView view = new(
            gpu,
            graph,
            pools,
            budget.Value,
            counts.Value,
            instanceRows.Value,
            retestArguments.Value,
            visibleClusters.Value,
            drawArguments.Value,
            retestList.Value,
            visibility.Value,
            glassVisibility.Value,
            hzb.Value
        );
        ClusterConstants clusters = new()
        {
            HzbLevels = (uint)view.HzbLevels,
            HasHzb = gpu.HasHistory ? 1u : 0u,
            ErrorThreshold = ErrorThresholdPixels,
            ImpostorPixels = impostorPixels,
            ImpostorDistance = impostorDistance,
            MaxVisibleClusters = MaxVisibleClusters,
            Clusters = pools.Clusters.Id,
            Indices = pools.Indices.Id,
            Vertices = pools.Vertices.Id,
            Models = pools.Models.Id,
            VisibleClusters = view.VisibleClusters.Id,
            DrawArguments = view.DrawArguments.Id,
            Counts = view.Counts.Id,
            RetestList = view.RetestList.Id,
            RetestArguments = view.RetestArguments.Id,
            Hzb = view.Hzb.Id,
            Visibility = view.Visibility.Id,
            GlassVisibility = view.GlassVisibility.Id,
        };
        graph.Upload(gpu.Frame, [clusters], ClusterConstants.Offset);

        return Result.Success(view);
    }

    /// <summary>
    /// The cluster budget set, the main view drawn twice, what last frame's pyramid shows, then what it hid and this
    /// frame's shows, the nearest glass in front of it drawn into the glass's buffer, and every pixel's material
    /// resolved into the GBuffer.
    /// </summary>
    public void Record(in RenderView view)
    {
        // Budget: last frame's count steers the frame's handoff and error threshold, then the counts start again.
        view.Graph.Compute(
                "Budget clusters",
                _budgetShader,
                new GpuConstants(view.Gpu.Frame.Id, 0, view.Budget.Id),
                RenderStage.Geometry
            )
            .Writes(view.Gpu.Frame, view.Counts, view.Budget)
            .Dispatch(1, 1);

        // Cull: one group an instance over its clusters, counted by distance, the nearest given their rows while the
        // list has room and the rest drawn as their impostors, what last frame's pyramid hid onto the retest list.
        uint across = (uint)Math.Min(view.Gpu.InstanceCount, CullGroupsPerRow);
        uint down = (uint)Math.Max((view.Gpu.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow, 1);
        view.Graph.Compute(
                "Count clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModeCount, view.InstanceRows.Id),
                RenderStage.Geometry
            )
            .Reads(view.Gpu.Frame, view.Gpu.Instances, view.Pools.Clusters, view.Pools.Models, view.Hzb)
            .Writes(view.Counts, view.InstanceRows)
            .Dispatch(across, down);
        view.Graph.Compute(
                "Fit clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModeFit),
                RenderStage.Geometry
            )
            .Reads(view.Gpu.Frame)
            .Writes(view.Counts)
            .Dispatch(1, 1);
        view.Graph.Compute(
                "Place clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModePlace, view.InstanceRows.Id),
                RenderStage.Geometry
            )
            .Reads(
                view.Gpu.Frame,
                view.Gpu.Instances,
                view.Gpu.Materials,
                view.Pools.Clusters,
                view.Pools.Models,
                view.Hzb,
                view.InstanceRows
            )
            .Writes(view.Counts, view.VisibleClusters, view.DrawArguments, view.RetestList, view.RetestArguments)
            .Dispatch(across, down);
        RecordDraw(view, _drawShaders, "Draw visibility", GetTargets(view, TargetLoad.Clear), DrawRegion.Visible);
        RecordDraw(view, _fadingShaders, "Draw fading", GetTargets(view, TargetLoad.Keep), DrawRegion.Fading);
        ImmutableArray<GpuResource> heapTextures = [.. view.Gpu.HeapTextures, .. view.Pools.Atlases];
        RecordImpostors(view, heapTextures, "Draw impostors", DrawRegion.Impostors);
        RecordHzb(view, GpuTexture.None);

        // Retest the list against the new pyramid, as many groups as the cull left in the retest arguments.
        view.Graph.Compute(
                "Retest clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModeRetest),
                RenderStage.Geometry
            )
            .Reads(
                view.Gpu.Frame,
                view.Gpu.Instances,
                view.Gpu.Materials,
                view.Pools.Clusters,
                view.Pools.Models,
                view.Hzb,
                view.RetestList,
                view.VisibleClusters
            )
            .Writes(view.Counts, view.DrawArguments)
            .Indirect(view.RetestArguments)
            .DispatchIndirect(view.RetestArguments, 0);
        RecordDraw(view, _drawShaders, "Draw retested", GetTargets(view, TargetLoad.Keep), DrawRegion.Retested);
        RecordDraw(
            view,
            _fadingShaders,
            "Draw retested fading",
            GetTargets(view, TargetLoad.Keep),
            DrawRegion.RetestedFading
        );
        RecordImpostors(view, heapTextures, "Draw retested impostors", DrawRegion.RetestedImpostors);

        // The picture cleared before the material resolve writes emissive light into it, so a pixel that no resolve
        // writes this frame, while its pass builds or after it failed, adds none.
        RecordHzb(view, view.Gpu.Hdr);

        // The nearest translucent surface in front of the opaque ones, from both passes' draws, where the view has glass.
        if (!view.GlassVisibility.IsNone)
        {
            RecordDraw(
                view,
                _glassShaders,
                "Draw glass",
                new GpuRenderTargets
                {
                    Colors = [new GpuTarget { Texture = view.GlassVisibility, Load = TargetLoad.Clear }],
                    Depth = new GpuTarget { Texture = view.Gpu.GlassDepth, Load = TargetLoad.Clear },
                },
                DrawRegion.Glass,
                view.Gpu.Depth
            );
        }

        // Resolve each pixel's material into the GBuffer, with its emissive light in the picture for the shading to add
        // to, a pass for each material shader, which resolves the pixels whose rows it draws, and the panes it draws into
        // the glass targets where the view has them.
        ReadOnlySpan<ShaderEntry> resolves = GetResolveShaders(view.Gpu.MaterialShaders);
        for (int shader = 0; shader < resolves.Length; shader++)
        {
            view.Graph.Compute(
                    "Resolve materials",
                    resolves[shader],
                    new GpuConstants(view.Gpu.Frame.Id, view.Pools.Impostors.Id, (uint)shader),
                    RenderStage.Geometry
                )
                .Reads(
                    view.Gpu.Frame,
                    view.Gpu.Instances,
                    view.Pools.Impostors,
                    view.Pools.Clusters,
                    view.Pools.Models,
                    view.Gpu.Vertices,
                    view.Pools.Vertices,
                    view.Pools.Indices,
                    view.Gpu.Materials,
                    view.VisibleClusters,
                    view.Visibility,
                    view.GlassVisibility
                )
                .Reads(heapTextures.AsSpan())
                .Writes(
                    view.Gpu.GBufferNormal,
                    view.Gpu.GBufferBase,
                    view.Gpu.GBufferSurface,
                    view.Gpu.Velocity,
                    view.Gpu.Hdr
                )
                .Writes(view.Gpu.GlassBase, view.Gpu.GlassNormal, view.Gpu.GlassSurface)
                .DispatchOver(view.Gpu.Width, view.Gpu.Height);
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
    /// The view's visibility buffer and depth, kept or cleared.
    /// </summary>
    private static GpuRenderTargets GetTargets(in RenderView view, TargetLoad load) =>
        new()
        {
            Colors = [new GpuTarget { Texture = view.Visibility, Load = load }],
            Depth = new GpuTarget { Texture = view.Gpu.Depth, Load = load },
        };

    /// <summary>
    /// The visible clusters of <paramref name="region"/> of the draws, drawn with <paramref name="shaders"/> into
    /// <paramref name="targets"/>, one indirect draw each, the shaders reading <paramref name="reads"/> beside the
    /// clusters.
    /// </summary>
    private static void RecordDraw(
        in RenderView view,
        in DrawShaders shaders,
        string name,
        in GpuRenderTargets targets,
        DrawRegion region,
        params ReadOnlySpan<GpuResource> reads
    ) =>
        DrawIndirect(
            view.Graph.Draw(name, shaders, targets, new GpuConstants(view.Gpu.Frame.Id), RenderStage.Geometry)
                .Reads(
                    view.Gpu.Frame,
                    view.Gpu.Instances,
                    view.Gpu.Vertices,
                    view.Pools.Vertices,
                    view.Pools.Clusters,
                    view.Pools.Models,
                    view.Pools.Indices,
                    view.VisibleClusters
                )
                .Reads(reads),
            view,
            region
        );

    /// <summary>
    /// Ends <paramref name="draw"/> with an indirect draw of each the cull left in <paramref name="region"/> of the
    /// draws, as many as it counted at the region's word of the counts.
    /// </summary>
    private static void DrawIndirect(DrawPass draw, in RenderView view, DrawRegion region) =>
        draw.Indirect(view.DrawArguments, view.Counts)
            .DrawIndirectCount(
                view.DrawArguments,
                (ulong)region * MaxVisibleClusters * DrawArgumentsBytes,
                view.Counts,
                (ulong)region * sizeof(uint),
                MaxVisibleClusters
            );

    /// <summary>
    /// The impostors the cull left in <paramref name="region"/> of the draws, into the view's visibility buffer and
    /// depth, one indirect draw each, reading their atlases among <paramref name="heapTextures"/>.
    /// </summary>
    private void RecordImpostors(
        in RenderView view,
        ImmutableArray<GpuResource> heapTextures,
        string name,
        DrawRegion region
    ) =>
        DrawIndirect(
            view.Graph.Draw(
                    name,
                    _impostorShaders,
                    GetTargets(view, TargetLoad.Keep),
                    new GpuConstants(view.Gpu.Frame.Id, 0, view.Pools.Impostors.Id),
                    RenderStage.Geometry
                )
                .Reads(view.Gpu.Frame, view.Gpu.Instances, view.Pools.Impostors, view.Pools.Models)
                .Reads(heapTextures.AsSpan()),
            view,
            region
        );

    /// <summary>
    /// The depth pyramid from the depth so far, a level a pass, the first also clearing <paramref name="picture"/>
    /// unless it is none.
    /// </summary>
    private void RecordHzb(in RenderView view, GpuTexture picture)
    {
        for (int level = 0; level < view.HzbLevels; level++)
        {
            ComputePass build = view
                .Graph.Compute(
                    $"Build depth pyramid {level}",
                    _buildHzbShader,
                    new GpuConstants(view.Gpu.Frame.Id, (uint)level, picture.Id),
                    RenderStage.Geometry
                )
                .Reads(view.Gpu.Frame);
            if (level == 0)
                build.Reads(view.Gpu.Depth).Writes(picture);
            else
                build.Reads(view.Hzb, level - 1);

            build.Writes(view.Hzb, level);
            build.DispatchOver(Math.Max(view.Gpu.Width >> level, 1), Math.Max(view.Gpu.Height >> level, 1));
        }
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
