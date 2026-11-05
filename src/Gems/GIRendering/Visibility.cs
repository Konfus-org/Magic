using System.Runtime.InteropServices;
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
    private readonly GpuPass _buildHzbPass = pipelines.Compute(
        "Build depth pyramid",
        "BuildHzb",
        "BuildHzb",
        isTiled: true
    );
    private readonly GpuPass _resolvePass = pipelines.Compute(
        "Resolve standard material",
        "ResolveMaterial",
        "ResolveMaterial",
        PassShaders.Standard,
        isTiled: true
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
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }
    }

    public Result Create()
    {
        Result<GpuBuffer> counts = CreateBuffer("Counts", CountsBytes, BufferUsages.Storage | BufferUsages.Indirect);
        Result<GpuBuffer> retestArguments = CreateBuffer(
            "Retest arguments",
            RetestArgumentsBytes,
            BufferUsages.Storage | BufferUsages.Indirect
        );
        Result<GpuBuffer> visibleClusters = CreateBuffer(
            "Visible clusters",
            (long)MaxVisibleClusters * 8,
            BufferUsages.Storage
        );
        Result<GpuBuffer> drawArguments = CreateBuffer(
            "Draw arguments",
            (long)MaxVisibleClusters * 3 * DrawArgumentsBytes,
            BufferUsages.Storage | BufferUsages.Indirect
        );
        Result<GpuBuffer> retestList = CreateBuffer("Retest list", (long)MaxVisibleClusters * 8, BufferUsages.Storage);
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
        GpuUpload retest = gpu.BeginUpload(RetestArgumentsBytes);
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(retest.Bytes);
        words[0] = 0;
        words[1] = 1;
        words[2] = 1;
        graph.Use(RetestArguments, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(retest.Buffer, retest.Offset, RetestArguments, 0, RetestArgumentsBytes);
    }

    /// <summary>
    /// The main view drawn twice: what last frame's pyramid shows, then what it hid and this frame's shows.
    /// </summary>
    public void RecordMain(in RenderView view)
    {
        RecordCull(view, "Cull clusters", mode: 0);
        RecordDraw(view, "Draw visibility", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordHzb(view);
        RecordCull(view, "Retest clusters", mode: 1);
        RecordDraw(
            view,
            "Draw retested",
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordHzb(view);
    }

    /// <summary>
    /// A single view drawn with no retest, as a card's is.
    /// </summary>
    public void RecordOnce(in RenderView view, string name)
    {
        RecordCull(view, $"{name} cull", mode: 0);
        RecordDraw(view, $"{name} draw", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordResolve(view, $"{name} resolve");
    }

    public void RecordResolve(in RenderView view, string name) =>
        _resolvePass
            .Run(new GpuConstants(view.Constants.Id), name: name)
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
            .Dispatch(pipelines.GetTiles(view.Targets.Width), pipelines.GetTiles(view.Targets.Height));

    /// <summary>
    /// What the cull left for the translucent passes to draw.
    /// </summary>
    public DrawList GetTranslucentDraws(in GpuConstants header, GpuBuffer frameConstants, GpuBuffer instances) =>
        new(
            header,
            frameConstants,
            instances,
            DrawArguments,
            (ulong)MaxVisibleClusters * 2 * DrawArgumentsBytes,
            Counts,
            16,
            MaxVisibleClusters,
            DrawArgumentsBytes,
            pools.Vertices,
            pools.Indices,
            pools.Clusters,
            materials.Buffer,
            VisibleClusters
        );

    private Result<GpuBuffer> CreateBuffer(string name, long bytes, BufferUsages usage)
    {
        Result<GpuBuffer> buffer = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = bytes,
                Usage = usage,
                Name = name,
            }
        );

        return buffer.Ok ? buffer : Result.Failure($"the {name} buffer was not made: {buffer.Error}");
    }

    /// <summary>
    /// The cluster cull: mode 0 one group an instance over its clusters, mode 1 the retest list, as many groups as
    /// mode 0 left in the retest arguments.
    /// </summary>
    private void RecordCull(in RenderView view, string name, uint mode)
    {
        GpuPassRun cullRun = _cullPass
            .Run(new GpuConstants(view.Constants.Id, mode), name: name)
            .Reads(view.Constants, view.Instances, pools.Clusters)
            .Writes(Counts, VisibleClusters, DrawArguments)
            .Reads(view.Targets.Hzb);
        if (mode == 0)
        {
            int rows = (view.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow;
            cullRun
                .Writes(RetestList, RetestArguments)
                .Dispatch((uint)Math.Min(view.InstanceCount, CullGroupsPerRow), (uint)Math.Max(rows, 1));
        }
        else
        {
            cullRun.Reads(RetestList).Indirect(RetestArguments).DispatchIndirect(RetestArguments, 0);
        }
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
                    Colors = [new GpuColorTarget { Texture = view.Targets.Visibility, Load = load }],
                    Depth = new GpuDepthTarget { Texture = view.Targets.Depth, Load = load },
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
            int width = Math.Max(targets.Width >> level, 1);
            int height = Math.Max(targets.Height >> level, 1);
            buildHzbRun.Dispatch(pipelines.GetTiles(width), pipelines.GetTiles(height));
        }
    }
}
