using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// One view's buffers and targets, the targets all the view's size: what its constants name and the passes that draw it
/// take. <paramref name="HasHistory"/> when the last frame drew the view at this size, so its place, depth pyramid and
/// shadows are there to carry on from.
/// </summary>
internal readonly record struct RenderView(
    GpuBuffer Constants,
    GpuBuffer Instances,
    GpuBuffer Materials,
    int InstanceCount,
    CullBuffers Cull,
    int Width,
    int Height,
    bool HasHistory,
    GpuTexture Visibility,
    GpuTexture Depth,
    GpuTexture Hzb,
    GpuTexture GBufferNormal,
    GpuTexture GBufferBase,
    GpuTexture GBufferSurface,
    GpuTexture Velocity,
    GpuTexture Hdr,
    GpuTexture Output,
    RenderGraph Graph
)
{
    public int HzbLevels => TextureDescription.GetFullLevels(Width, Height);
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
internal sealed class Visibility(AssetManager assets, GpuResources resources)
{
    public const int MaxVisibleClusters = 1 << 18;

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
    private readonly ShaderEntry _resolveShader = PassShaders.GetEntry(
        assets,
        "ResolveMaterial",
        "ResolveMaterial"
    ) with
    {
        TypeArguments = [PassShaders.GetStandard(assets)],
    };

    public ImmutableArray<ShaderEntry> Shaders =>
        [
            _budgetShader,
            _cullShader,
            _drawShaders.Vertex,
            _drawShaders.Pixel,
            _fadingShaders.Pixel,
            _impostorShaders.Vertex,
            _impostorShaders.Pixel,
            _buildHzbShader,
            _resolveShader,
        ];

    /// <summary>
    /// The cull's buffers asked for, which the frame's constants name, then the retest dispatch at no groups, in a
    /// transfer pass. The budget pass zeroes the counts once it has read last frame's.
    /// </summary>
    public static Result<CullBuffers> Prepare(RenderGraph graph)
    {
        Result<GpuBuffer> budget = graph.Buffer(GpuDescriptions.GetBuffer("Cluster budget", BudgetBytes));
        Result<GpuBuffer> counts = graph.Buffer(GpuDescriptions.GetBuffer("Counts", CountsBytes, IndirectUsage));
        Result<GpuBuffer> instanceRows = graph.Buffer(
            GpuDescriptions.GetBuffer("Instance rows", (long)InstanceTable.Capacity * sizeof(uint))
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
                (long)MaxVisibleClusters * 4 * DrawArgumentsBytes,
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
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [
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

        return Result.Success(
            new CullBuffers(
                budget.Value,
                counts.Value,
                instanceRows.Value,
                retestArguments.Value,
                visibleClusters.Value,
                drawArguments.Value,
                retestList.Value,
                impostorDraws.Value
            )
        );
    }

    /// <summary>
    /// The cluster budget set, the main view drawn twice, what last frame's pyramid shows, then what it hid and this
    /// frame's shows, and every pixel's material resolved into the GBuffer.
    /// </summary>
    public void Record(in RenderView view)
    {
        CullBuffers cull = view.Cull;

        // Budget: last frame's count steers the frame's handoff and error threshold, then the counts start again.
        view.Graph.Compute("Budget clusters", _budgetShader, new GpuConstants(view.Constants.Id, 0, cull.Budget.Id))
            .Writes(view.Constants, cull.Counts, cull.Budget)
            .Dispatch(1, 1);

        // Cull: one group an instance over its clusters, counted by distance, the nearest given their rows while the
        // list has room and the rest drawn as their impostors, what last frame's pyramid hid onto the retest list.
        uint across = (uint)Math.Min(view.InstanceCount, CullGroupsPerRow);
        uint down = (uint)Math.Max((view.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow, 1);
        view.Graph.Compute(
                "Count clusters",
                _cullShader,
                new GpuConstants(view.Constants.Id, ModeCount, cull.ImpostorDraws.Id, cull.InstanceRows.Id)
            )
            .Reads(view.Constants, view.Instances, resources.ClusterPool, view.Hzb)
            .Writes(cull.Counts, cull.InstanceRows)
            .Dispatch(across, down);
        view.Graph.Compute("Fit clusters", _cullShader, new GpuConstants(view.Constants.Id, ModeFit))
            .Reads(view.Constants)
            .Writes(cull.Counts)
            .Dispatch(1, 1);
        view.Graph.Compute(
                "Place clusters",
                _cullShader,
                new GpuConstants(view.Constants.Id, ModePlace, cull.ImpostorDraws.Id, cull.InstanceRows.Id)
            )
            .Reads(view.Constants, view.Instances, resources.ClusterPool, view.Hzb, cull.InstanceRows)
            .Writes(
                cull.Counts,
                cull.VisibleClusters,
                cull.DrawArguments,
                cull.RetestList,
                cull.RetestArguments,
                cull.ImpostorDraws
            )
            .Dispatch(across, down);
        RecordDraw(view, _drawShaders, "Draw visibility", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordDraw(
            view,
            _fadingShaders,
            "Draw fading",
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * 2 * DrawArgumentsBytes,
            countOffset: 24
        );
        ImmutableArray<GpuResource> heapTextures = resources.GetHeapTextures(view.Graph);
        RecordImpostors(view, heapTextures, "Draw impostors", drawsOffset: 0, countOffset: 16);
        RecordHzb(view);

        // Retest the list against the new pyramid, as many groups as the cull left in the retest arguments.
        view.Graph.Compute(
                "Retest clusters",
                _cullShader,
                new GpuConstants(view.Constants.Id, ModeRetest, cull.ImpostorDraws.Id)
            )
            .Reads(
                view.Constants,
                view.Instances,
                resources.ClusterPool,
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
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordDraw(
            view,
            _fadingShaders,
            "Draw retested fading",
            TargetLoad.Keep,
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

        // Resolve each pixel's material into the GBuffer.
        view.Graph.Compute(
                "Resolve standard material",
                _resolveShader,
                new GpuConstants(view.Constants.Id, resources.ImpostorTable.Id)
            )
            .Reads(
                view.Constants,
                view.Instances,
                resources.ImpostorTable,
                resources.ClusterPool,
                resources.VertexPool,
                resources.IndexPool,
                view.Materials,
                cull.VisibleClusters,
                view.Visibility
            )
            .Reads(heapTextures.AsSpan())
            .Writes(view.GBufferNormal, view.GBufferBase, view.GBufferSurface, view.Velocity)
            .DispatchOver(view.Width, view.Height);
    }

    /// <summary>
    /// The visible clusters of one region of the draws, drawn with <paramref name="shaders"/> into the view's visibility
    /// buffer and its depth, one indirect draw each.
    /// </summary>
    private void RecordDraw(
        in RenderView view,
        in DrawShaders shaders,
        string name,
        TargetLoad load,
        ulong drawsOffset,
        ulong countOffset
    ) =>
        view
            .Graph.Draw(
                name,
                shaders,
                new GpuRenderTargets
                {
                    Colors = [new GpuTarget { Texture = view.Visibility, Load = load }],
                    Depth = new GpuTarget { Texture = view.Depth, Load = load },
                },
                new GpuConstants(view.Constants.Id)
            )
            .Reads(
                view.Constants,
                view.Instances,
                resources.VertexPool,
                resources.ClusterPool,
                resources.IndexPool,
                view.Cull.VisibleClusters
            )
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
                    Depth = new GpuTarget { Texture = view.Depth, Load = TargetLoad.Keep },
                },
                new GpuConstants(view.Constants.Id, 0, resources.ImpostorTable.Id)
            )
            .Reads(view.Constants, view.Instances, resources.ImpostorTable)
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
                    new GpuConstants(view.Constants.Id, (uint)level)
                )
                .Reads(view.Constants);
            if (level == 0)
                build.Reads(view.Depth);
            else
                build.Reads(view.Hzb, level - 1);

            build.Writes(view.Hzb, level);
            build.DispatchOver(Math.Max(view.Width >> level, 1), Math.Max(view.Height >> level, 1));
        }
    }
}
