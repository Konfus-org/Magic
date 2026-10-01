using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Systems.Rendering;

/// <summary>
/// The GPU culling, as commands: the pages whose cell touches the frustum listed, then one compute group per listed
/// page deciding per instance and counting survivors into the draw args. With occlusion on, the early pass also
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
    /// An instance whose bounding sphere projects to a radius under this many pixels is culled, like one outside the
    /// frustum. Higher culls more of the small and distant instances: fewer triangles and draws, but things pop in
    /// later and visibly once it is past a few pixels. Lower draws them down to specks that cost vertex work and
    /// shimmer; 0 draws everything in the frustum.
    /// </summary>
    public const float MinObjectPixels = 2f;

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

        GpuPipeline pageCull = Compute("Cull/PageCull.comp.hlsl");
        if (!ctx.Settings.OcclusionCulling)
            return new CullPipelines(pageCull, Compute("Cull/CullEarly.comp.hlsl"), default, default, default);

        return new CullPipelines(pageCull, Compute("Cull/CullEarly.comp.hlsl", "#define OCCLUSION 1\n"),
            Compute("Cull/SeedLateArgs.comp.hlsl"), Compute("Cull/CullLate.comp.hlsl"), Compute("Cull/HiZBuild.comp.hlsl"));
    }

    /// <summary>The buffers of the view at <paramref name="index"/> in this frame's view list.</summary>
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
    /// rest of the frame's data: the early draw args as the template (no instances yet), no visible pages, no candidates.
    /// </summary>
    public static void Prepare(RenderContext ctx, ViewBuffers view, int viewWidth, int viewHeight)
    {
        IRendering gpu = ctx.Gpu;
        view.HiZCurrent ^= 1;
        uint argsBytes = Math.Max((uint)ctx.Buckets.ChunkCount * Buckets.ChunkBytes, Buckets.ChunkBytes);
        view.DrawArgsEarly.Ensure(gpu, argsBytes);
        view.VisibleIds.Ensure(gpu, ctx.Buckets.VisibleHighWater * 4);
        view.VisiblePages.Ensure(gpu, Math.Max(16, (uint)ctx.Instances.PageCount * 4));

        ReadOnlySpan<DrawArgs> template = ctx.Buckets.TemplateRows;
        gpu.Upload(view.DrawArgsEarly.Handle, 0, template[..Math.Min(template.Length, (int)(argsBytes / DrawArgs.Size))]);
        gpu.Upload<uint>(view.DispatchArgs.Handle, 0, [0, 1, 1]);
        if (!ctx.Settings.OcclusionCulling)
            return;

        view.Candidates!.Ensure(gpu, (ctx.Buckets.VisibleHighWater + 4) * 4);
        gpu.Upload<uint>(view.Candidates.Handle, 0, [0]);
        view.DrawArgsLate!.Ensure(gpu, argsBytes);
        ResizeHiZ(ctx, view, viewWidth, viewHeight);
    }

    /// <summary>The early compute passes for one view, its buffers made ready by <see cref="Prepare"/>. Outside any pass. Returns the dispatches recorded.</summary>
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
        if (ctx.Settings.OcclusionCulling)
        {
            Dispatch(commands, ctx.Cull.CullEarly,
                [view.DrawArgsEarly.Handle, view.VisibleIds.Handle, view.Candidates!.Handle],
                [view.VisiblePages.Handle, instances.PageBuffer.Handle, instances.CullBuffer.Handle, view.HiZPrevious!.Handle],
                0, indirect: view.DispatchArgs.Handle);
        }
        else
        {
            Dispatch(commands, ctx.Cull.CullEarly,
                [view.DrawArgsEarly.Handle, view.VisibleIds.Handle],
                [view.VisiblePages.Handle, instances.PageBuffer.Handle, instances.CullBuffer.Handle],
                0, indirect: view.DispatchArgs.Handle);
        }

        return 2;
    }

    /// <summary>After the early draws: builds this frame's pyramid from the depth target and retests the candidates. Returns the dispatches recorded.</summary>
    public static int RecordLate(RenderContext ctx, RenderCommands commands, ViewBuffers view, in FrameConstants frame, GpuTexture depth)
    {
        int dispatches = 0;

        // One compute pass per six levels: the first six from the depth target, each later six from the level before them.
        FrameConstants f = frame;
        int lw = view.HiZWidth, lh = view.HiZHeight;
        ReadOnlySpan<GpuBinding> depthBinding = [new GpuBinding(Texture: depth, Sampler: ctx.NearestClamp)];
        for (int first = 0; first < view.HiZLevels; first += HiZLevelsPerPass)
        {
            f.HiZFirstLevel = (uint)first;
            commands.Push(GpuStage.Compute, f);
            Dispatch(commands, ctx.Cull.HiZBuild, [view.HiZThisFrame!.Handle], [],
                (uint)((lw + HiZTile - 1) / HiZTile), (uint)((lh + HiZTile - 1) / HiZTile), samplers: depthBinding);
            dispatches++;

            for (int l = 0; l < HiZLevelsPerPass; l++)
            {
                lw = (lw + 1) / 2;
                lh = (lh + 1) / 2;
            }
        }

        commands.Push(GpuStage.Compute, frame);
        uint slots = (uint)ctx.Buckets.ChunkCount * Buckets.GroupsPerChunk;

        // The late args start as the template with each group's first instance moved past the early count.
        Dispatch(commands, ctx.Cull.SeedLate,
            [view.DrawArgsLate!.Handle, view.DispatchArgsLate!.Handle],
            [ctx.Buckets.Template.Handle, view.DrawArgsEarly.Handle, view.Candidates!.Handle],
            (slots + 63) / 64);

        Dispatch(commands, ctx.Cull.CullLate,
            [view.DrawArgsLate.Handle, view.VisibleIds.Handle],
            [view.Candidates.Handle, ctx.Instances.CullBuffer.Handle, view.HiZThisFrame!.Handle],
            0, indirect: view.DispatchArgsLate.Handle);

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

    private static void ResizeHiZ(RenderContext ctx, ViewBuffers view, int viewWidth, int viewHeight)
    {
        int w = Math.Max(1, (viewWidth + 1) / 2), h = Math.Max(1, (viewHeight + 1) / 2);
        if (w == view.HiZWidth && h == view.HiZHeight)
            return;

        view.HiZWidth = w;
        view.HiZHeight = h;
        view.HiZLevels = 1;

        int floats = 0;
        int lw = w, lh = h;
        while (true)
        {
            floats += lw * lh;
            if ((lw == 1 && lh == 1) || view.HiZLevels == MaxHiZLevels)
                break;
            lw = (lw + 1) / 2;
            lh = (lh + 1) / 2;
            view.HiZLevels++;
        }

        if (ctx.Zeros.Length < floats)
            ctx.Zeros = new float[floats];

        foreach (GrowableBuffer pyramid in view.HiZ)
        {
            pyramid.Ensure(ctx.Gpu, (uint)floats * 4);
            ctx.Gpu.Upload(pyramid.Handle, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(ctx.Zeros.AsSpan(0, floats)));
        }
    }
}
