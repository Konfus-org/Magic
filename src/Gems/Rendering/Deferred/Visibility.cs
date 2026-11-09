using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.Deferred;

/// <summary>
/// The view as the world on the GPU posted it this frame, the graph its passes record into, and the targets and
/// buffers the clusters are drawn through, all the view's size.
/// </summary>
internal readonly record struct RenderView(
    GpuView Gpu,
    RenderGraph Graph,
    CullBuffers Cull,
    GpuTexture Visibility,
    GpuTexture GlassVisibility,
    GpuTexture Hzb
)
{
    public int HzbLevels => TextureDescription.GetFullLevels(Gpu.Width, Gpu.Height);
}

/// <summary>
/// The cull's buffers: what each pass of it counts, with the rows asked for by distance, the rows each instance asks
/// for, the visible clusters and their draws, what last frame's depth pyramid hid, to retest, and the cluster budget's
/// level, which carries from frame to frame.
/// </summary>
internal readonly record struct CullBuffers(
    GpuBuffer Budget,
    GpuBuffer Counts,
    GpuBuffer InstanceRows,
    GpuBuffer RetestArguments,
    GpuBuffer VisibleClusters,
    GpuBuffer DrawArguments,
    GpuBuffer RetestList,
    GpuBuffer ImpostorDraws
);

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
    /// no groups, and the cluster constants that name them uploaded after the shadows' row. The budget pass zeroes the
    /// counts once it has read last frame's.
    /// </summary>
    public static Result<RenderView> Prepare(
        RenderGraph graph,
        in GpuView gpu,
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
                (long)MaxVisibleClusters * 5 * DrawArgumentsBytes,
                IndirectUsage
            )
        );
        Result<GpuBuffer> retestList = graph.Buffer(
            GpuDescriptions.GetBuffer("Retest list", (long)MaxVisibleClusters * 8)
        );
        Result<GpuBuffer> impostorDraws = graph.Buffer(
            GpuDescriptions.GetBuffer(
                "Impostor draws",
                (long)MaxVisibleClusters * 2 * DrawArgumentsBytes,
                IndirectUsage
            )
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
        Result<GpuTexture> glassVisibility = graph.Texture(
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
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [
                    visibility,
                    glassVisibility,
                    hzb,
                    budget,
                    counts,
                    instanceRows,
                    retestArguments,
                    visibleClusters,
                    drawArguments,
                    retestList,
                    impostorDraws,
                ]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        graph.Upload(retestArguments.Value, [0u, 1u, 1u]);
        CullBuffers cull = new(
            budget.Value,
            counts.Value,
            instanceRows.Value,
            retestArguments.Value,
            visibleClusters.Value,
            drawArguments.Value,
            retestList.Value,
            impostorDraws.Value
        );
        RenderView view = new(gpu, graph, cull, visibility.Value, glassVisibility.Value, hzb.Value);
        ClusterConstants clusters = new()
        {
            HzbLevels = (uint)view.HzbLevels,
            HasHzb = gpu.HasHistory ? 1u : 0u,
            ErrorThreshold = ErrorThresholdPixels,
            ImpostorPixels = impostorPixels,
            ImpostorDistance = impostorDistance,
            MaxVisibleClusters = MaxVisibleClusters,
            Clusters = gpu.ClusterPool.Id,
            Models = gpu.ClusterModels.Id,
            VisibleClusters = cull.VisibleClusters.Id,
            DrawArguments = cull.DrawArguments.Id,
            Counts = cull.Counts.Id,
            RetestList = cull.RetestList.Id,
            RetestArguments = cull.RetestArguments.Id,
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
        CullBuffers cull = view.Cull;

        // Budget: last frame's count steers the frame's handoff and error threshold, then the counts start again.
        view.Graph.Compute(
                "Budget clusters",
                _budgetShader,
                new GpuConstants(view.Gpu.Frame.Id, 0, cull.Budget.Id),
                RenderStage.Geometry
            )
            .Writes(view.Gpu.Frame, cull.Counts, cull.Budget)
            .Dispatch(1, 1);

        // Cull: one group an instance over its clusters, counted by distance, the nearest given their rows while the
        // list has room and the rest drawn as their impostors, what last frame's pyramid hid onto the retest list.
        uint across = (uint)Math.Min(view.Gpu.InstanceCount, CullGroupsPerRow);
        uint down = (uint)Math.Max((view.Gpu.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow, 1);
        view.Graph.Compute(
                "Count clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModeCount, cull.ImpostorDraws.Id, cull.InstanceRows.Id),
                RenderStage.Geometry
            )
            .Reads(view.Gpu.Frame, view.Gpu.Instances, view.Gpu.ClusterPool, view.Gpu.ClusterModels, view.Hzb)
            .Writes(cull.Counts, cull.InstanceRows)
            .Dispatch(across, down);
        view.Graph.Compute(
                "Fit clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModeFit),
                RenderStage.Geometry
            )
            .Reads(view.Gpu.Frame)
            .Writes(cull.Counts)
            .Dispatch(1, 1);
        view.Graph.Compute(
                "Place clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModePlace, cull.ImpostorDraws.Id, cull.InstanceRows.Id),
                RenderStage.Geometry
            )
            .Reads(
                view.Gpu.Frame,
                view.Gpu.Instances,
                view.Gpu.Materials,
                view.Gpu.ClusterPool,
                view.Gpu.ClusterModels,
                view.Hzb,
                cull.InstanceRows
            )
            .Writes(
                cull.Counts,
                cull.VisibleClusters,
                cull.DrawArguments,
                cull.RetestList,
                cull.RetestArguments,
                cull.ImpostorDraws
            )
            .Dispatch(across, down);
        RecordDraw(
            view,
            _drawShaders,
            "Draw visibility",
            GetTargets(view, TargetLoad.Clear),
            drawsOffset: 0,
            countOffset: 0
        );
        RecordDraw(
            view,
            _fadingShaders,
            "Draw fading",
            GetTargets(view, TargetLoad.Keep),
            drawsOffset: (ulong)MaxVisibleClusters * 2 * DrawArgumentsBytes,
            countOffset: 24
        );
        ImmutableArray<GpuResource> heapTextures = view.Gpu.HeapTextures;
        RecordImpostors(view, heapTextures, "Draw impostors", drawsOffset: 0, countOffset: 16);
        RecordHzb(view);

        // Retest the list against the new pyramid, as many groups as the cull left in the retest arguments.
        view.Graph.Compute(
                "Retest clusters",
                _cullShader,
                new GpuConstants(view.Gpu.Frame.Id, ModeRetest, cull.ImpostorDraws.Id),
                RenderStage.Geometry
            )
            .Reads(
                view.Gpu.Frame,
                view.Gpu.Instances,
                view.Gpu.Materials,
                view.Gpu.ClusterPool,
                view.Gpu.ClusterModels,
                view.Hzb,
                cull.RetestList,
                cull.VisibleClusters
            )
            .Writes(cull.Counts, cull.DrawArguments, cull.ImpostorDraws)
            .Indirect(cull.RetestArguments)
            .DispatchIndirect(cull.RetestArguments, 0);
        RecordDraw(
            view,
            _drawShaders,
            "Draw retested",
            GetTargets(view, TargetLoad.Keep),
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordDraw(
            view,
            _fadingShaders,
            "Draw retested fading",
            GetTargets(view, TargetLoad.Keep),
            drawsOffset: (ulong)MaxVisibleClusters * 3 * DrawArgumentsBytes,
            countOffset: 28
        );
        RecordImpostors(
            view,
            heapTextures,
            "Draw retested impostors",
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 20
        );
        RecordHzb(view);

        // The nearest translucent surface in front of the opaque ones, from both passes' draws.
        RecordDraw(
            view,
            _glassShaders,
            "Draw glass",
            new GpuRenderTargets
            {
                Colors = [new GpuTarget { Texture = view.GlassVisibility, Load = TargetLoad.Clear }],
                Depth = new GpuTarget { Texture = view.Gpu.GlassDepth, Load = TargetLoad.Clear },
            },
            drawsOffset: (ulong)MaxVisibleClusters * 4 * DrawArgumentsBytes,
            countOffset: 40,
            view.Gpu.Depth
        );

        // Resolve each pixel's material into the GBuffer, a pass for each material shader, which resolves the pixels whose
        // rows it draws.
        ReadOnlySpan<ShaderEntry> resolves = GetResolveShaders(view.Gpu.MaterialShaders);
        for (int shader = 0; shader < resolves.Length; shader++)
        {
            RecordResolve(view, heapTextures, shader);
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

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resolveShaders);
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
    /// The visible clusters of one region of the draws, drawn with <paramref name="shaders"/> into
    /// <paramref name="targets"/>, one indirect draw each, the shaders reading <paramref name="reads"/> beside the
    /// clusters.
    /// </summary>
    private static void RecordDraw(
        in RenderView view,
        in DrawShaders shaders,
        string name,
        in GpuRenderTargets targets,
        ulong drawsOffset,
        ulong countOffset,
        params ReadOnlySpan<GpuResource> reads
    ) =>
        view
            .Graph.Draw(name, shaders, targets, new GpuConstants(view.Gpu.Frame.Id), RenderStage.Geometry)
            .Reads(
                view.Gpu.Frame,
                view.Gpu.Instances,
                view.Gpu.Vertices,
                view.Gpu.ClusterPool,
                view.Gpu.ClusterModels,
                view.Gpu.Indices,
                view.Cull.VisibleClusters
            )
            .Reads(reads)
            .Indirect(view.Cull.DrawArguments, view.Cull.Counts)
            .DrawIndirectCount(
                view.Cull.DrawArguments,
                drawsOffset,
                view.Cull.Counts,
                countOffset,
                MaxVisibleClusters,
                DrawArgumentsBytes
            );

    /// <summary>
    /// The impostors the cull left at <paramref name="drawsOffset"/>, into the view's visibility buffer and depth,
    /// one indirect draw each, reading their atlases among <paramref name="heapTextures"/>.
    /// </summary>
    private void RecordImpostors(
        in RenderView view,
        ImmutableArray<GpuResource> heapTextures,
        string name,
        ulong drawsOffset,
        ulong countOffset
    ) =>
        view
            .Graph.Draw(
                name,
                _impostorShaders,
                new GpuRenderTargets
                {
                    Colors = [new GpuTarget { Texture = view.Visibility, Load = TargetLoad.Keep }],
                    Depth = new GpuTarget { Texture = view.Gpu.Depth, Load = TargetLoad.Keep },
                },
                new GpuConstants(view.Gpu.Frame.Id, 0, view.Gpu.ImpostorTable.Id),
                RenderStage.Geometry
            )
            .Reads(view.Gpu.Frame, view.Gpu.Instances, view.Gpu.ImpostorTable, view.Gpu.ClusterModels)
            .Reads(heapTextures.AsSpan())
            .Indirect(view.Cull.ImpostorDraws, view.Cull.Counts)
            .DrawIndirectCount(
                view.Cull.ImpostorDraws,
                drawsOffset,
                view.Cull.Counts,
                countOffset,
                MaxVisibleClusters,
                DrawArgumentsBytes
            );

    /// <summary>
    /// The depth pyramid from the depth so far, a level a pass.
    /// </summary>
    private void RecordHzb(in RenderView view)
    {
        for (int level = 0; level < view.HzbLevels; level++)
        {
            ComputePass build = view
                .Graph.Compute(
                    $"Build depth pyramid {level}",
                    _buildHzbShader,
                    new GpuConstants(view.Gpu.Frame.Id, (uint)level),
                    RenderStage.Geometry
                )
                .Reads(view.Gpu.Frame);
            if (level == 0)
                build.Reads(view.Gpu.Depth);
            else
                build.Reads(view.Hzb, level - 1);

            build.Writes(view.Hzb, level);
            build.DispatchOver(Math.Max(view.Gpu.Width >> level, 1), Math.Max(view.Gpu.Height >> level, 1));
        }
    }

    /// <summary>
    /// The pixels whose rows material shader <paramref name="shader"/> draws, resolved into the GBuffer, and the panes
    /// it draws into the glass targets.
    /// </summary>
    private void RecordResolve(in RenderView view, ImmutableArray<GpuResource> heapTextures, int shader) =>
        view
            .Graph.Compute(
                "Resolve materials",
                _resolveShaders[shader],
                new GpuConstants(view.Gpu.Frame.Id, view.Gpu.ImpostorTable.Id, (uint)shader),
                RenderStage.Geometry
            )
            .Reads(
                view.Gpu.Frame,
                view.Gpu.Instances,
                view.Gpu.ImpostorTable,
                view.Gpu.ClusterPool,
                view.Gpu.ClusterModels,
                view.Gpu.Vertices,
                view.Gpu.Indices,
                view.Gpu.Materials,
                view.Cull.VisibleClusters,
                view.Visibility,
                view.GlassVisibility
            )
            .Reads(heapTextures.AsSpan())
            .Writes(view.Gpu.GBufferNormal, view.Gpu.GBufferBase, view.Gpu.GBufferSurface, view.Gpu.Velocity)
            .Writes(view.Gpu.GlassBase, view.Gpu.GlassNormal, view.Gpu.GlassSurface)
            .DispatchOver(view.Gpu.Width, view.Gpu.Height);
}
