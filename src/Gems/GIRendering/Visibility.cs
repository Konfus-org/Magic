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
/// The cull's buffers: what each pass of it counts, the visible clusters and their draws, and what last frame's depth
/// pyramid hid, to retest.
/// </summary>
internal readonly record struct CullBuffers(
    GpuBuffer Counts,
    GpuBuffer RetestArguments,
    GpuBuffer VisibleClusters,
    GpuBuffer DrawArguments,
    GpuBuffer RetestList
);

/// <summary>
/// What a view sees: every instance's clusters culled on the GPU against the frustum, their cones, the pixel error
/// threshold and last frame's depth pyramid; the visible ones drawn into a visibility buffer; what the old pyramid hid
/// retested against the new one; and every pixel's material resolved into the GBuffer.
/// </summary>
internal sealed class Visibility(AssetManager assets, GpuResources resources)
{
    public const int MaxVisibleClusters = 1 << 18;

    private const int CountsBytes = 64;
    private const int DrawArgumentsBytes = 16;
    private const int RetestArgumentsBytes = 12;
    private const int CullGroupsPerRow = 65535;
    private const BufferUsages IndirectUsage = BufferUsages.Storage | BufferUsages.Indirect;

    private readonly ShaderEntry _cullShader = PassShaders.GetEntry(assets, "CullClusters", "CullClusters");
    private readonly DrawShaders _drawShaders = new(
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawVisibility", "DrawVisibilityPixel", ShaderStage.Pixel)
    )
    {
        Depth = DepthMode.TestAndWrite,
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
        [_cullShader, _drawShaders.Vertex, _drawShaders.Pixel, _buildHzbShader, _resolveShader];

    /// <summary>
    /// The cull's buffers asked for, which the frame's constants name, then the counts at zero and the retest dispatch at
    /// no groups, in a transfer pass.
    /// </summary>
    public static Result<CullBuffers> Prepare(RenderGraph graph)
    {
        Result<GpuBuffer> counts = graph.Buffer(GpuDescriptions.GetBuffer("Counts", CountsBytes, IndirectUsage));
        Result<GpuBuffer> retestArguments = graph.Buffer(
            GpuDescriptions.GetBuffer("Retest arguments", RetestArgumentsBytes, IndirectUsage)
        );
        Result<GpuBuffer> visibleClusters = graph.Buffer(
            GpuDescriptions.GetBuffer("Visible clusters", (long)MaxVisibleClusters * 8)
        );
        Result<GpuBuffer> drawArguments = graph.Buffer(
            GpuDescriptions.GetBuffer(
                "Draw arguments",
                (long)MaxVisibleClusters * 2 * DrawArgumentsBytes,
                IndirectUsage
            )
        );
        Result<GpuBuffer> retestList = graph.Buffer(
            GpuDescriptions.GetBuffer("Retest list", (long)MaxVisibleClusters * 8)
        );
        foreach (
            Result made in (ReadOnlySpan<Result>)[counts, retestArguments, visibleClusters, drawArguments, retestList]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        graph.Clear(counts.Value);
        graph.Upload(retestArguments.Value, [0u, 1u, 1u]);

        return Result.Success(
            new CullBuffers(
                counts.Value,
                retestArguments.Value,
                visibleClusters.Value,
                drawArguments.Value,
                retestList.Value
            )
        );
    }

    /// <summary>
    /// The main view drawn twice, what last frame's pyramid shows, then what it hid and this frame's shows, and every
    /// pixel's material resolved into the GBuffer.
    /// </summary>
    public void Record(in RenderView view)
    {
        CullBuffers cull = view.Cull;

        // Cull: one group an instance over its clusters, what last frame's pyramid hid onto the retest list.
        int rows = (view.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow;
        view.Graph.Compute("Cull clusters", _cullShader, new GpuConstants(view.Constants.Id, 0))
            .Reads(view.Constants, view.Instances, resources.ClusterPool, view.Hzb)
            .Writes(cull.Counts, cull.VisibleClusters, cull.DrawArguments, cull.RetestList, cull.RetestArguments)
            .Dispatch((uint)Math.Min(view.InstanceCount, CullGroupsPerRow), (uint)Math.Max(rows, 1));
        RecordDraw(view, "Draw visibility", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordHzb(view);

        // Retest the list against the new pyramid, as many groups as the cull left in the retest arguments.
        view.Graph.Compute("Retest clusters", _cullShader, new GpuConstants(view.Constants.Id, 1))
            .Reads(view.Constants, view.Instances, resources.ClusterPool, view.Hzb, cull.RetestList)
            .Writes(cull.Counts, cull.VisibleClusters, cull.DrawArguments)
            .Indirect(cull.RetestArguments)
            .DispatchIndirect(cull.RetestArguments, 0);
        RecordDraw(
            view,
            "Draw retested",
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordHzb(view);

        // Resolve each pixel's material into the GBuffer.
        view.Graph.Compute("Resolve standard material", _resolveShader, new GpuConstants(view.Constants.Id))
            .Reads(
                view.Constants,
                view.Instances,
                resources.ClusterPool,
                resources.VertexPool,
                resources.IndexPool,
                view.Materials,
                cull.VisibleClusters,
                view.Visibility
            )
            .Writes(view.GBufferNormal, view.GBufferBase, view.GBufferSurface, view.Velocity)
            .DispatchOver(view.Width, view.Height);
    }

    /// <summary>
    /// The visible clusters drawn into the view's visibility buffer and its depth, one indirect draw each.
    /// </summary>
    private void RecordDraw(in RenderView view, string name, TargetLoad load, ulong drawsOffset, ulong countOffset) =>
        view
            .Graph.Draw(
                name,
                _drawShaders,
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
