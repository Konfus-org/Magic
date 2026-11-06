using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// One view's buffers and targets: what the passes that draw a view take.
/// </summary>
internal readonly record struct RenderView(
    GpuBuffer Constants,
    GpuBuffer Instances,
    int InstanceCount,
    FrameTargets Targets
);

/// <summary>
/// What a view sees: every instance's clusters culled on the GPU against the frustum, their cones, the pixel error
/// threshold and last frame's depth pyramid; the visible ones drawn into a visibility buffer; what the old pyramid hid
/// retested against the new one; and every pixel's material resolved into the GBuffer.
/// </summary>
internal sealed class Visibility(
    IGraphicsApi gpu,
    RenderGraph graph,
    GpuPipelines pipelines,
    GeometryPools pools,
    MaterialTable materials
) : IDisposable
{
    public const int MaxVisibleClusters = 1 << 18;

    private const int CountsBytes = 64;
    private const int DrawArgumentsBytes = 16;
    private const int RetestArgumentsBytes = 12;
    private const int CullGroupsPerRow = 65535;

    private readonly GpuPass _cullPass = pipelines.Compute("Cull clusters", "CullClusters", "CullClusters");
    private readonly GpuPass _drawPass = pipelines.Draw(
        "Draw visibility",
        "DrawVisibility",
        "DrawVisibilityVertex",
        "DrawVisibilityPixel",
        state =>
            state with
            {
                ColorFormats = [GpuFormat.R32Uint],
                DepthFormat = GpuFormat.D32Sfloat,
                Depth = DepthMode.TestAndWrite,
            }
    );
    private readonly GpuPass _buildHzbPass = pipelines.Compute("Build depth pyramid", "BuildHzb", "BuildHzb");
    private readonly GpuPass _resolvePass = pipelines.Compute(
        "Resolve standard material",
        "ResolveMaterial",
        "ResolveMaterial",
        PassShaders.Standard
    );

    public GpuBuffer Counts { get; private set; }

    public GpuBuffer RetestArguments { get; private set; }

    public GpuBuffer VisibleClusters { get; private set; }

    public GpuBuffer DrawArguments { get; private set; }

    public GpuBuffer RetestList { get; private set; }

    public void Dispose()
    {
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [Counts, RetestArguments, VisibleClusters, DrawArguments, RetestList]
        )
        {
            gpu.Destroy(buffer);
        }
    }

    public Result Create()
    {
        Result<GpuBuffer> counts = gpu.CreateBuffer(
            "Counts",
            CountsBytes,
            BufferUsages.Storage | BufferUsages.Indirect
        );
        Result<GpuBuffer> retestArguments = gpu.CreateBuffer(
            "Retest arguments",
            RetestArgumentsBytes,
            BufferUsages.Storage | BufferUsages.Indirect
        );
        Result<GpuBuffer> visibleClusters = gpu.CreateBuffer("Visible clusters", (long)MaxVisibleClusters * 8);
        Result<GpuBuffer> drawArguments = gpu.CreateBuffer(
            "Draw arguments",
            (long)MaxVisibleClusters * 2 * DrawArgumentsBytes,
            BufferUsages.Storage | BufferUsages.Indirect
        );
        Result<GpuBuffer> retestList = gpu.CreateBuffer("Retest list", (long)MaxVisibleClusters * 8);
        foreach (
            Result<GpuBuffer> made in (ReadOnlySpan<Result<GpuBuffer>>)
                [counts, retestArguments, visibleClusters, drawArguments, retestList]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        Counts = counts.Value;
        RetestArguments = retestArguments.Value;
        VisibleClusters = visibleClusters.Value;
        DrawArguments = drawArguments.Value;
        RetestList = retestList.Value;

        return Result.Success();
    }

    /// <summary>
    /// The counts at zero and the retest dispatch at no groups, in a transfer pass.
    /// </summary>
    public void ResetCounts()
    {
        graph.Use(Counts, GpuAccess.CopyDestination);
        graph.Recorder.FillBuffer(Counts, 0);
        graph.Upload(gpu, RetestArguments, [0u, 1u, 1u]);
    }

    /// <summary>
    /// The main view drawn twice, what last frame's pyramid shows, then what it hid and this frame's shows, and every
    /// pixel's material resolved into the GBuffer.
    /// </summary>
    public void Record(in RenderView view)
    {
        // Cull: one group an instance over its clusters, what last frame's pyramid hid onto the retest list.
        int rows = (view.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow;
        _cullPass
            .Run(new GpuConstants(view.Constants.Id, 0))
            .Reads(view.Constants, view.Instances, pools.Clusters, view.Targets.Hzb)
            .Writes(Counts, VisibleClusters, DrawArguments, RetestList, RetestArguments)
            .Dispatch((uint)Math.Min(view.InstanceCount, CullGroupsPerRow), (uint)Math.Max(rows, 1));
        RecordDraw(view, "Draw visibility", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordHzb(view);

        // Retest the list against the new pyramid, as many groups as the cull left in the retest arguments.
        _cullPass
            .Run(new GpuConstants(view.Constants.Id, 1), "Retest clusters")
            .Reads(view.Constants, view.Instances, pools.Clusters, view.Targets.Hzb, RetestList)
            .Writes(Counts, VisibleClusters, DrawArguments)
            .Indirect(RetestArguments)
            .DispatchIndirect(RetestArguments, 0);
        RecordDraw(
            view,
            "Draw retested",
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordHzb(view);

        // Resolve each pixel's material into the GBuffer.
        _resolvePass
            .Run(new GpuConstants(view.Constants.Id))
            .Reads(
                view.Constants,
                view.Instances,
                pools.Clusters,
                pools.Vertices,
                pools.Indices,
                materials.Buffer,
                VisibleClusters,
                view.Targets.Visibility
            )
            .Writes(
                view.Targets.GBufferNormal,
                view.Targets.GBufferBase,
                view.Targets.GBufferSurface,
                view.Targets.Velocity
            )
            .DispatchOver(view.Targets.Width, view.Targets.Height);
    }

    /// <summary>
    /// The visible clusters drawn into the view's visibility buffer and its depth, one indirect draw each.
    /// </summary>
    private void RecordDraw(in RenderView view, string name, TargetLoad load, ulong drawsOffset, ulong countOffset) =>
        _drawPass
            .Run(
                new GpuConstants(view.Constants.Id),
                new GpuRenderTargets
                {
                    Colors = [new GpuTarget { Texture = view.Targets.Visibility, Load = load }],
                    Depth = new GpuTarget { Texture = view.Targets.Depth, Load = load },
                },
                name: name
            )
            .Reads(view.Constants, view.Instances, pools.Vertices, pools.Clusters, pools.Indices, VisibleClusters)
            .Indirect(DrawArguments, Counts)
            .DrawIndirectCount(DrawArguments, drawsOffset, Counts, countOffset, MaxVisibleClusters, DrawArgumentsBytes);

    /// <summary>
    /// The depth pyramid from the depth so far, a level a pass.
    /// </summary>
    private void RecordHzb(in RenderView view)
    {
        FrameTargets targets = view.Targets;
        for (int level = 0; level < targets.HzbLevels; level++)
        {
            GpuPassRun buildHzbRun = _buildHzbPass
                .Run(new GpuConstants(view.Constants.Id, (uint)level), name: $"Build depth pyramid {level}")
                .Reads(view.Constants);
            if (level == 0)
                buildHzbRun.Reads(targets.Depth);
            else
                buildHzbRun.Reads(targets.Hzb, level - 1);

            buildHzbRun.Writes(targets.Hzb, level);
            buildHzbRun.DispatchOver(Math.Max(targets.Width >> level, 1), Math.Max(targets.Height >> level, 1));
        }
    }
}
