using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Systems.Rendering;

/// <summary>
/// The GPU culling, as commands: the pages whose cell touches the frustum listed, then one compute group per listed
/// page deciding per instance and counting survivors into the draw args: an instance small on screen into those of a
/// lesser version of its mesh (<see cref="GpuLodRow"/>), and into both while it blends from one to the other
/// (<see cref="LodBlend"/>). With occlusion on, the early pass also
/// holds back what last frame's depth pyramid hides; after the early draws the pyramid is rebuilt from the depth target and
/// a late pass retests those candidates, so anything that came out from behind something is drawn the same frame. Every
/// dependent step is its own compute pass (no barriers inside one). The pyramid is a buffer of floats, level after level,
/// level 0 at half the view's resolution (sizes rounded up), each level the farthest depth of the texels it covers;
/// buffer-backed so building levels from the one before never reads and writes one texture in the same pass. One dispatch
/// builds <see cref="HiZLevelsPerPass"/> levels, the later ones in group shared memory. Without occlusion there are no
/// pyramids and no late pass at all.
/// </summary>
internal static class Culling
{
    /// <summary>
    /// An instance whose cull radius (that of its bounding sphere, unless its renderer says otherwise) projects to
    /// under this many pixels is culled, like one outside the frustum. Higher culls more of the small and distant instances: fewer triangles and draws, but things pop in
    /// later and visibly once it is past a few pixels. Lower draws them down to specks that cost vertex work and
    /// shimmer; 0 draws everything in the frustum.
    /// </summary>
    public const float MinObjectPixels = 1f;

    /// <summary>
    /// The share of a LOD threshold over which an instance blends from one version of its mesh into the next instead
    /// of switching: from the threshold down to this much of it further, both are drawn, the finer one dithered out as
    /// the lesser one is dithered in. Higher blends over a longer stretch, so it is smoother but more instances are
    /// drawn twice and the dither shows for longer; 0 switches at once. Under 1.
    /// </summary>
    public const float LodBlend = 0.25f;

    private const int MaxHiZLevels = 12;

    private const int HiZLevelsPerPass = 6; // HiZBuild: a 32 x 32 tile per group, halved five times

    private const int HiZTile = 32;

    /// <summary>
    /// Compiles the culling compute shaders; the late-pass ones only with occlusion, which the early cull is compiled
    /// for too (<c>OCCLUSION</c>): without it that shader has no pyramid and no candidate list to bind.
    /// </summary>
    public static CullPipelines Create(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        GpuPipeline Compute(string path, string defines = "") => gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, path, GpuStage.Compute, defines));

        string lodBlend = FormattableString.Invariant($"#define LOD_BLEND {LodBlend:0.0#####}\n");
        GpuPipeline pageCull = Compute("Cull/PageCull.comp.hlsl");
        if (!ctx.Settings.OcclusionCulling)
            return new CullPipelines(pageCull, Compute("Cull/CullEarly.comp.hlsl", lodBlend), default, default, default);

        return new CullPipelines(pageCull, Compute("Cull/CullEarly.comp.hlsl", lodBlend + "#define OCCLUSION 1\n"),
            Compute("Cull/SeedLateArgs.comp.hlsl"), Compute("Cull/CullLate.comp.hlsl", lodBlend), Compute("Cull/HiZBuild.comp.hlsl"));
    }

    /// <summary>
    /// The buffers of the view at <paramref name="index"/> in this frame's view list.
    /// </summary>
    public static ViewBuffers View(RenderContext ctx, int index)
    {
        while (ctx.Views.Count <= index)
            ctx.Views.Add(new ViewBuffers(ctx.Gpu, ctx.Settings.OcclusionCulling));

        return ctx.Views[index];
    }

    /// <summary>
    /// Before a frame is recorded: sizes the view's buffers for this frame's buckets, visible ids and pages, and its
    /// pyramids for the view's size (when that changed both are zeroed: nothing hidden), and turns the pyramids over,
    /// last frame's "current" becoming "previous". What the passes count into starts the frame reset, uploaded with the
    /// rest of the frame's data: the early draw args as the template (no instances yet), no visible pages (and how many pages there are to look
    /// through), no candidates.
    /// </summary>
    public static void Prepare(RenderContext ctx, ViewBuffers view, int viewWidth, int viewHeight)
    {
        IRendering gpu = ctx.Gpu;
        uint argsBytes = Math.Max((uint)ctx.Buckets.ChunkCount * Buckets.ChunkBytes, Buckets.ChunkBytes);
        view.DrawArgsEarly.Ensure(gpu, argsBytes);
        view.VisibleIds.Ensure(gpu, ctx.Buckets.VisibleHighWater * GpuVisible.Size);
        view.VisiblePages.Ensure(gpu, Math.Max(16, (uint)ctx.Instances.PageCount * 4));

        ReadOnlySpan<DrawArgs> template = ctx.Buckets.TemplateRows;
        gpu.Upload(view.DrawArgsEarly.Handle, 0, template[..Math.Min(template.Length, (int)(argsBytes / DrawArgs.Size))]);
        gpu.Upload<uint>(view.DispatchArgs.Handle, 0, [0, 1, 1, (uint)ctx.Instances.PageCount]);
        if (view.Occlusion is not { } occlusion)
            return;

        occlusion.TurnOver();
        occlusion.Candidates.Ensure(gpu, (ctx.Buckets.VisibleHighWater + 4) * 4);
        gpu.Upload<uint>(occlusion.Candidates.Handle, 0, [0]);
        occlusion.DrawArgsLate.Ensure(gpu, argsBytes);
        ResizeHiZ(ctx, view, occlusion, viewWidth, viewHeight);
    }

    /// <summary>
    /// The early compute passes for one view, its buffers made ready by <see cref="Prepare"/>. Outside any pass. Returns the dispatches recorded.
    /// </summary>
    public static int RecordEarly(RenderContext ctx, RenderCommands commands, ViewBuffers view, in FrameConstants frame)
    {
        InstanceTable instances = ctx.Instances;

        commands.Push(GpuStage.Compute, frame);

        // 1. The pages whose cell touches the frustum.
        Dispatch(commands, ctx.Cull.PageCull,
            [view.VisiblePages.Handle, view.DispatchArgs.Handle],
            [instances.PageBuffer.Handle, instances.CellBuffer.Handle],
            ((uint)instances.PageCount + 63) / 64);

        // 2. Instances of those pages, one group per page; with occlusion, last frame's pyramid decides the candidates.
        if (view.Occlusion is { } occlusion)
        {
            Dispatch(commands, ctx.Cull.CullEarly,
                [view.DrawArgsEarly.Handle, view.VisibleIds.Handle, occlusion.Candidates.Handle],
                [view.VisiblePages.Handle, instances.PageBuffer.Handle, instances.CullBuffer.Handle, ctx.Buckets.Lods.Handle, occlusion.Previous.Handle],
                0, indirect: view.DispatchArgs.Handle);
        }
        else
        {
            Dispatch(commands, ctx.Cull.CullEarly,
                [view.DrawArgsEarly.Handle, view.VisibleIds.Handle],
                [view.VisiblePages.Handle, instances.PageBuffer.Handle, instances.CullBuffer.Handle, ctx.Buckets.Lods.Handle],
                0, indirect: view.DispatchArgs.Handle);
        }

        return 2;
    }

    /// <summary>
    /// After the early draws: builds this frame's pyramid from the depth target and retests the candidates. Returns the dispatches recorded.
    /// </summary>
    public static int RecordLate(RenderContext ctx, RenderCommands commands, ViewBuffers view, in FrameConstants frame, GpuTexture depth)
    {
        if (view.Occlusion is not { } occlusion)
            return 0;

        int dispatches = 0;

        // One compute pass per six levels: the first six from the depth target, each later six from the level before them.
        FrameConstants constants = frame;
        int levelWidth = view.HiZWidth, levelHeight = view.HiZHeight;
        ReadOnlySpan<GpuBinding> depthBinding = [new GpuBinding(Texture: depth, Sampler: ctx.NearestClamp)];
        for (int first = 0; first < view.HiZLevels; first += HiZLevelsPerPass)
        {
            constants.HiZFirstLevel = (uint)first;
            commands.Push(GpuStage.Compute, constants);
            Dispatch(commands, ctx.Cull.HiZBuild, [occlusion.ThisFrame.Handle], [],
                (uint)((levelWidth + HiZTile - 1) / HiZTile), (uint)((levelHeight + HiZTile - 1) / HiZTile), samplers: depthBinding);
            dispatches++;

            for (int level = 0; level < HiZLevelsPerPass; level++)
            {
                levelWidth = (levelWidth + 1) / 2;
                levelHeight = (levelHeight + 1) / 2;
            }
        }

        commands.Push(GpuStage.Compute, frame);
        uint slots = (uint)ctx.Buckets.ChunkCount * Buckets.GroupsPerChunk;

        // The late args start as the template with each group's first instance moved past the early count.
        Dispatch(commands, ctx.Cull.SeedLate,
            [occlusion.DrawArgsLate.Handle, occlusion.DispatchArgsLate.Handle],
            [ctx.Buckets.Template.Handle, view.DrawArgsEarly.Handle, occlusion.Candidates.Handle],
            (slots + 63) / 64);

        Dispatch(commands, ctx.Cull.CullLate,
            [occlusion.DrawArgsLate.Handle, view.VisibleIds.Handle],
            [occlusion.Candidates.Handle, ctx.Instances.CullBuffer.Handle, occlusion.ThisFrame.Handle, ctx.Buckets.Lods.Handle],
            0, indirect: occlusion.DispatchArgsLate.Handle);

        return dispatches + 2;
    }

    /// <summary>
    /// One compute pass: the read-write buffers, the pipeline, the read-only buffers and samplers, then <paramref name="x"/>
    /// × <paramref name="y"/> groups or, when <paramref name="indirect"/> is set, the groups it holds at offset 0.
    /// </summary>
    public static void Dispatch(
        RenderCommands commands,
        GpuPipeline pipeline,
        ReadOnlySpan<GpuBuffer> writes,
        ReadOnlySpan<GpuBuffer> reads,
        uint x,
        uint y = 1,
        GpuBuffer indirect = default,
        ReadOnlySpan<GpuBinding> samplers = default)
    {
        Span<GpuBinding> rw = stackalloc GpuBinding[writes.Length];
        for (int i = 0; i < writes.Length; i++)
            rw[i] = new GpuBinding(writes[i]);

        commands.BeginComputePass(rw);
        commands.BindPipeline(pipeline);
        if (!reads.IsEmpty)
            commands.BindStorageBuffers(GpuStage.Compute, 0, reads);
        if (!samplers.IsEmpty)
            commands.BindTextures(GpuStage.Compute, 0, samplers);

        if (indirect.IsValid)
            commands.DispatchIndirect(indirect);
        else
            commands.Dispatch(x, y);
        commands.EndComputePass();
    }

    private static void ResizeHiZ(RenderContext ctx, ViewBuffers view, OcclusionBuffers occlusion, int viewWidth, int viewHeight)
    {
        int width = Math.Max(1, (viewWidth + 1) / 2), height = Math.Max(1, (viewHeight + 1) / 2);
        if (width == view.HiZWidth && height == view.HiZHeight)
            return;

        view.HiZWidth = width;
        view.HiZHeight = height;
        view.HiZLevels = 1;

        int floats = 0;
        int levelWidth = width, levelHeight = height;
        while (true)
        {
            floats += levelWidth * levelHeight;
            if ((levelWidth == 1 && levelHeight == 1) || view.HiZLevels == MaxHiZLevels)
                break;
            levelWidth = (levelWidth + 1) / 2;
            levelHeight = (levelHeight + 1) / 2;
            view.HiZLevels++;
        }

        if (ctx.Zeros.Length < floats)
            ctx.Zeros = new float[floats];

        foreach (GrowableBuffer pyramid in occlusion.Pyramids)
        {
            pyramid.Ensure(ctx.Gpu, (uint)floats * 4);
            ctx.Gpu.Upload(pyramid.Handle, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(ctx.Zeros.AsSpan(0, floats)));
        }
    }
}
