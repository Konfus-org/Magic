using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Magic.Systems.Rendering;

/// <summary>
/// A frame of the scene, in two steps. <see cref="Plan"/> decides it: which render targets, which views in each and
/// their rectangles, with every texture and buffer sized and every view's constants built. <see cref="Record"/> then
/// turns the plan into commands and changes nothing: for each target, cull, draw early, with occlusion build the pyramid
/// and draw late, run the data passes, and present: blit the result to the window's swapchain image, or into every mip
/// level of the render texture's pool layer. Render textures go first, so a window drawn after them this frame samples
/// what they show now. A main window no camera draws into is cleared, so whatever a gem draws over it later lands on
/// something defined.
/// </summary>
internal static class Scene
{
    private const int MaxResolution = 16384;

    /// <summary>
    /// Fills <paramref name="plan"/> with this frame: every render target a view draws into, each once, render textures
    /// first and then windows, each in view order, with its textures sized to it and the views that draw into it, next
    /// to each other. Window 0 is the main window, so a target is always named by its real handle.
    /// </summary>
    public static void Plan(
        RenderContext ctx,
        FramePlan plan,
        IWindowRegistry? windows,
        ReadOnlySpan<View> views,
        in LightingConstants lighting,
        float time)
    {
        plan.Clear();
        uint main = windows?.Main?.Handle ?? 0;
        PlanTargets(ctx, plan, windows, views, main, textures: true, lighting, time);
        PlanTargets(ctx, plan, windows, views, main, textures: false, lighting, time);

        if (main != 0 && !Planned(plan, RenderTarget.Of(main)))
            plan.ClearWindow = main;
    }

    /// <summary>The plan as commands. Returns the draws and dispatches recorded.</summary>
    public static (int Draws, int Dispatches) Record(RenderContext ctx, RenderCommands commands, FramePlan plan)
    {
        (int draws, int dispatches) = (0, 0);
        ReadOnlySpan<ViewPlan> views = CollectionsMarshal.AsSpan(plan.Views);
        foreach (TargetPlan target in plan.Targets)
        {
            (int d, int c) = RecordTarget(ctx, commands, target.Targets, views.Slice(target.FirstView, target.ViewCount));
            draws += d;
            dispatches += c;
        }

        if (plan.ClearWindow != 0)
        {
            commands.BeginRenderPass(GpuTexture.Window(plan.ClearWindow), GpuLoad.Clear);
            commands.EndRenderPass();
        }

        return (draws, dispatches);
    }

    /// <summary>The targets of one kind (render textures, or windows) that are not planned yet, each with its passes' outputs and its views.</summary>
    private static void PlanTargets(
        RenderContext ctx,
        FramePlan plan,
        IWindowRegistry? windows,
        ReadOnlySpan<View> views,
        uint main,
        bool textures,
        in LightingConstants lighting,
        float time)
    {
        foreach (View view in views)
        {
            RenderTarget target = Resolve(view.Camera.Target, main);
            if (target.IsTexture != textures || Planned(plan, target))
                continue;

            FrameTargets? targets = textures ? PlanTexture(ctx, target) : PlanWindow(ctx, windows, target);
            if (targets is null)
                continue;

            Passes.Prepare(ctx, targets);
            int first = plan.Views.Count;
            for (int v = 0; v < views.Length; v++)
            {
                if (Resolve(views[v].Camera.Target, main) == target)
                    plan.Views.Add(PlanView(ctx, views[v], v, targets, lighting, time));
            }

            plan.Targets.Add(new TargetPlan(targets, first, plan.Views.Count - first));
        }
    }

    /// <summary>One view of a target: its rectangle, its buffers made ready for this frame, and its constants.</summary>
    private static ViewPlan PlanView(RenderContext ctx, in View view, int index, FrameTargets targets, in LightingConstants lighting, float time)
    {
        Rectangle rect = view.Camera.Viewport.ToPixels((int)targets.Width, (int)targets.Height);
        ViewBuffers buffers = Culling.View(ctx, index);
        Culling.Prepare(ctx, buffers, rect.Width, rect.Height);
        FrameConstants constants = FrameConstants.Build(
            view, rect, buffers.HiZSize, time, Culling.MinObjectPixels, lighting);

        return new ViewPlan(index, rect, buffers, constants);
    }

    private static bool Planned(FramePlan plan, RenderTarget target)
    {
        foreach (TargetPlan planned in plan.Targets)
        {
            if (planned.Targets.RenderTarget == target)
                return true;
        }

        return false;
    }

    /// <summary>The window's targets at the settings' resolution (the window's own size without one); null when it is not open (warned once, its targets released) or minimised.</summary>
    private static FrameTargets? PlanWindow(RenderContext ctx, IWindowRegistry? windows, RenderTarget target)
    {
        if (target.Window == 0)
            return null; // no main window: headless

        IWindow? window = windows?.Get(target.Window);
        if (window is null || !window.IsOpen)
        {
            if (ctx.MissingTargets.Add(target))
                Debugging.Log.Warn($"A camera targets {target}, which is not open; it is skipped.");
            Forget(ctx, target);
            return null;
        }

        Size size = window.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
            return null; // minimised

        // Rendered at the settings' resolution, whatever the window's size; Present stretches the result over it.
        Size resolution = ctx.Settings.Resolution;
        if (resolution.Width > 0 && resolution.Height > 0)
            size = new Size(Math.Min(resolution.Width, MaxResolution), Math.Min(resolution.Height, MaxResolution));

        return Ensure(ctx, target, size.Width, size.Height);
    }

    /// <summary>
    /// The render texture's targets at its size; null while no material samples it (nothing would show what is drawn, so
    /// nothing is) or when it failed to load.
    /// </summary>
    private static FrameTargets? PlanTexture(RenderContext ctx, RenderTarget target)
    {
        if (!ctx.Textures.Rendered.TryGetValue(target.Texture.Id, out RenderedTexture rendered))
        {
            Forget(ctx, target);
            return null;
        }

        return Ensure(ctx, target, rendered.Width, rendered.Height);
    }

    private static FrameTargets Ensure(RenderContext ctx, RenderTarget target, int width, int height)
    {
        if (!ctx.Targets.TryGetValue(target, out FrameTargets? targets))
            ctx.Targets[target] = targets = new FrameTargets(target, ctx.Gpu.DepthFormat);

        targets.Ensure(ctx.Gpu, (uint)width, (uint)height);
        return targets;
    }

    private static void Forget(RenderContext ctx, RenderTarget target)
    {
        if (ctx.Targets.Remove(target, out FrameTargets? gone))
            gone.Release(ctx.Gpu);
    }

    /// <summary>Window 0 is the main window.</summary>
    private static RenderTarget Resolve(RenderTarget target, uint main)
    {
        return !target.IsTexture && target.Window == 0 ? RenderTarget.Of(main) : target;
    }

    /// <summary>
    /// One render target: cull and draw its views into its textures (early, then with occlusion the late pass), run the
    /// data passes, present. Returns the draws and dispatches recorded.
    /// </summary>
    private static (int Draws, int Dispatches) RecordTarget(RenderContext ctx, RenderCommands commands, FrameTargets targets, ReadOnlySpan<ViewPlan> views)
    {
        int draws = 0, dispatches = 0;

        // Culling first (compute passes cannot sit inside the render pass), one set per view.
        foreach (ref readonly ViewPlan view in views)
            dispatches += Culling.RecordEarly(ctx, commands, view.Buffers, view.Constants);

        // Early draws: everything the previous frame's pyramid did not hide.
        commands.BeginRenderPass(targets.Hdr.Texture, GpuLoad.Clear, targets.Depth.Texture);
        foreach (ref readonly ViewPlan view in views)
            draws += DrawView(ctx, commands, view, view.Buffers.DrawArgsEarly.Handle);
        commands.EndRenderPass();

        if (ctx.Settings.OcclusionCulling)
        {
            // This frame's pyramid from what was just drawn, then the candidates that were held back.
            foreach (ref readonly ViewPlan view in views)
                dispatches += Culling.RecordLate(ctx, commands, view.Buffers, view.Constants, targets.Depth.Texture);

            commands.BeginRenderPass(targets.Hdr.Texture, GpuLoad.Load, targets.Depth.Texture);
            foreach (ref readonly ViewPlan view in views)
                draws += DrawView(ctx, commands, view, view.Buffers.DrawArgsLate!.Handle);
            commands.EndRenderPass();
        }

        // Ldr is what the tonemap pass writes; without one (missing, or failed to compile) present the linear scene rather
        // than a stale image, so a broken pass never blanks the window.
        bool wroteLdr = Passes.Record(ctx, commands, targets, views[0].Constants, ref draws, ref dispatches);
        Present(ctx, commands, targets, wroteLdr ? targets.Ldr : targets.Hdr);

        return (draws, dispatches);
    }

    /// <summary>
    /// The finished frame to where it is shown: a window's swapchain image, or every mip level of the render texture's pool
    /// layer, each straight from <paramref name="shown"/> (so no level is read while it is written). A render texture of
    /// another shape is stretched into the square layer, and back again on whatever samples it.
    /// </summary>
    private static void Present(RenderContext ctx, RenderCommands commands, FrameTargets targets, FrameTargets.Target shown)
    {
        Rectangle area = new(0, 0, (int)targets.Width, (int)targets.Height);
        RenderTarget target = targets.RenderTarget;
        if (!target.IsTexture)
        {
            commands.Blit(shown.Texture, area, new TextureRegion(GpuTexture.Window(target.Window)));
            return;
        }

        RenderedTexture rendered = ctx.Textures.Rendered[target.Texture.Id];
        TextureTable.PoolClass pool = ctx.Textures.Pools[rendered.Class];
        uint size = (uint)pool.Size;
        for (uint level = 0; level < pool.Levels; level++, size = Math.Max(1, size / 2))
            commands.Blit(shown.Texture, area, new TextureRegion(pool.Texture, level, rendered.Layer, 0, 0, size, size));
    }

    /// <summary>
    /// Draws what the culler left for one view: for every pipeline class, one indirect call per chunk of its draw args, the
    /// vertex stage reading each instance's slot from the visible-id list through the instance-rate buffer. Nothing here
    /// knows how many instances there are. Returns the draw calls recorded.
    /// </summary>
    private static int DrawView(RenderContext ctx, RenderCommands commands, in ViewPlan view, GpuBuffer drawArgs)
    {
        if (ctx.Buckets.ChunkCount == 0)
            return 0;

        commands.SetViewport(view.Rect);
        commands.SetScissor(view.Rect);
        commands.Push(GpuStage.Vertex, view.Constants);
        commands.Push(GpuStage.Fragment, view.Constants);

        commands.BindVertexBuffers(0, [ctx.Meshes.VertexBuffer, view.Buffers.VisibleIds.Handle]);
        commands.BindIndexBuffer(ctx.Meshes.IndexBuffer, wide: true);
        commands.BindStorageBuffers(GpuStage.Vertex, 0, [ctx.Instances.XformBuffer.Handle, ctx.Instances.CullBuffer.Handle]);
        Span<GpuBinding> textures = stackalloc GpuBinding[TextureTable.Classes];
        Textures.Bindings(ctx, textures);
        commands.BindTextures(GpuStage.Fragment, 0, textures);
        commands.BindStorageBuffers(GpuStage.Fragment, 0, [ctx.Materials.Records.Handle]);

        int draws = 0;
        foreach ((PipelineClass cls, List<int> chunks) in ctx.Buckets.ByClass)
        {
            if (chunks.Count == 0)
                continue;

            GpuPipeline pipeline = Pipelines.Get(ctx, cls); // none only while the class's first compile is still running
            if (!pipeline.IsValid)
                continue;

            commands.BindPipeline(pipeline);
            foreach (int chunk in chunks)
            {
                if (ctx.Buckets.IsEmpty(chunk))
                    continue; // every group released: its 64 commands would draw nothing

                commands.DrawIndexedIndirect(drawArgs, (uint)chunk * Buckets.ChunkBytes, Buckets.GroupsPerChunk);
                draws++;
            }
        }

        return draws;
    }
}
