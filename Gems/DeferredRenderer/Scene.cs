using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// A frame of the scene, in two steps. <see cref="Plan"/> decides it: which render targets, which views in each and
/// their rectangles, with every texture and buffer sized and every view's constants built. <see cref="Record"/> then
/// turns the plan into commands and changes nothing: the frame-wide stages (shadows, GI), then for each target the
/// pipeline's scene stage (cull and draw its views into the gbuffer), its lighting stage (into Hdr), its sky,
/// transparency and post stages, and present: blit the result to the window's swapchain image, or into every mip
/// level of the render texture's pool layer. Render textures go first, so a window drawn after them this frame samples
/// what they show now. A main window no camera draws into is cleared, so whatever a gem draws over it later lands on
/// something defined.
/// </summary>
internal static class Scene
{
    private const int MaxResolution = 16384;

    /// <summary>
    /// An instance whose cull radius projects to under this many pixels is culled, like one outside the frustum: a half
    /// pixel radius, so only what is less than a pixel across goes. Anything a pixel or more across is drawn (a cheap
    /// lesser version of it far away), so distant things never thin out or pop in.
    /// </summary>
    private const float MinObjectPixels = 0.5f;

    /// <summary>
    /// Fills <paramref name="plan"/> with this frame: the pipeline synced to what the world names (<paramref name="pipeline"/>
    /// and <paramref name="posts"/>), then every render target a view draws into, each once, render textures first and
    /// then windows, each in view order, with its textures sized to it, the views that draw into it next to each other,
    /// and everything the passes create for it made ready. Window 0 is the main window, so a target is always named by
    /// its real handle.
    /// </summary>
    public static void Plan(
        RenderContext ctx,
        FramePlan plan,
        IWindowRegistry? windows,
        ReadOnlySpan<View> views,
        Handle<Pipeline> pipeline,
        PostList posts,
        in LightingConstants lighting,
        float time,
        long frameNumber)
    {
        plan.Clear();
        plan.ClearColor = lighting.HasSky ? new Vector4(lighting.Ambient, 1f) : RenderCommands.ClearColor;
        PipelineSync.Sync(ctx, pipeline, posts);
        plan.Counts = FrameCounts.Of(ctx, lighting.LightCount, (uint)UploadBrickJobs(ctx), lighting.ShadowCasters);
        ctx.Gpu.Upload<GpuCounts>(ctx.Counts.Handle, 0, [GpuCounts.From(plan.Counts, (uint)frameNumber)]);
        uint main = windows?.Main?.Handle ?? 0;
        PlanTargets(ctx, plan, windows, views, main, textures: true, lighting, time, frameNumber);
        PlanTargets(ctx, plan, windows, views, main, textures: false, lighting, time, frameNumber);

        if (plan.Views.Count > 0)
        {
            plan.MainView = Math.Max(0, plan.Views.FindIndex(view => view.Constants.IsOrthographic == 0));
            ViewPlan mainView = plan.Views[plan.MainView];
            plan.Views[plan.MainView] = mainView with { Constants = mainView.Constants with { Flags = mainView.Constants.Flags | FrameConstants.IsMainViewFlag } };
            PrepareFrameStages(ctx, plan);
        }

        if (main != 0 && !IsPlanned(plan, RenderTarget.Of(main)))
            plan.ClearWindow = main;
    }

    /// <summary>
    /// The plan as commands. Returns the draws and dispatches recorded.
    /// </summary>
    public static (int Draws, int Dispatches) Record(RenderContext ctx, RenderCommands commands, FramePlan plan)
    {
        (int draws, int dispatches) = (0, 0);
        ReadOnlySpan<ViewPlan> views = CollectionsMarshal.AsSpan(plan.Views);

        // The shadow maps and the GI first: every target's lighting reads them, and nothing in them depends on a target.
        if (views.Length > 0)
        {
            (int shadowDraws, int shadowDispatches) = PassExecutor.RecordFrame(ctx, commands, plan, PipelineStage.Shadows);
            draws += shadowDraws;
            dispatches += shadowDispatches;
            (int giDraws, int giDispatches) = PassExecutor.RecordFrame(ctx, commands, plan, PipelineStage.Gi);
            draws += giDraws;
            dispatches += giDispatches;
        }

        foreach (TargetPlan target in plan.Targets)
        {
            (int targetDraws, int targetDispatches) = RecordTarget(ctx, commands, plan, target.FrameTargets, views.Slice(target.FirstView, target.ViewCount));
            draws += targetDraws;
            dispatches += targetDispatches;
        }

        if (plan.ClearWindow != 0)
        {
            commands.BeginRenderPass(GpuTexture.Window(plan.ClearWindow), GpuLoad.Clear);
            commands.EndRenderPass();
        }

        return (draws, dispatches);
    }

    /// <summary>
    /// The targets of one kind (render textures, or windows) that are not planned yet, each with its views and what the
    /// passes create for it.
    /// </summary>
    private static void PlanTargets(
        RenderContext ctx,
        FramePlan plan,
        IWindowRegistry? windows,
        ReadOnlySpan<View> views,
        uint main,
        bool textures,
        in LightingConstants lighting,
        float time,
        long frameNumber)
    {
        foreach (View view in views)
        {
            RenderTarget target = Resolve(view.Camera.Target, main);
            if (target.IsTexture != textures || IsPlanned(plan, target))
                continue;

            FrameTargets? targets = textures ? PlanTexture(ctx, target) : PlanWindow(ctx, windows, target);
            if (targets is null)
                continue;

            int first = plan.Views.Count;
            for (int index = 0; index < views.Length; index++)
            {
                if (Resolve(views[index].Camera.Target, main) == target)
                    plan.Views.Add(PlanView(ctx, views[index], index, targets, lighting, time, frameNumber));
            }

            PrepareTargetStages(ctx, plan, targets, CollectionsMarshal.AsSpan(plan.Views)[first..]);
            plan.Targets.Add(new TargetPlan(targets, first, plan.Views.Count - first));
        }
    }

    /// <summary>
    /// One view of a target: its rectangle and its constants.
    /// </summary>
    private static ViewPlan PlanView(RenderContext ctx, in View view, int index, FrameTargets targets, in LightingConstants lighting, float time, long frameNumber)
    {
        Rectangle rect = view.Camera.Viewport.ToPixels((int)targets.Width, (int)targets.Height);
        (int hiZWidth, int hiZHeight, int hiZLevels, _) = HiZ.Size(rect.Width, rect.Height);
        FrameConstants constants = FrameConstants.Build(
            view, rect, (hiZWidth, hiZHeight, hiZLevels), time, MinObjectPixels, MathF.Max(0.01f, ctx.Lod.Bias), lighting,
            (uint)frameNumber, ((uint)index << FrameConstants.ViewIndexShift) | ((uint)ctx.Deferred.DebugView << FrameConstants.DebugViewShift));
        return new ViewPlan(index, rect, ctx.Resources.View(index), constants);
    }

    /// <summary>
    /// What the passes of the target and view stages create for this render target is made ready: a texture that follows
    /// the target's size among its targets (in use this frame, so the rest can go), everything else in the target's or the
    /// view's set, sized by this frame's counts. A pass that does not fit the target (its output is already in use in
    /// another format) is left out of it this frame.
    /// </summary>
    private static void PrepareTargetStages(RenderContext ctx, FramePlan plan, FrameTargets targets, ReadOnlySpan<ViewPlan> views)
    {
        PipelineState pipeline = ctx.Pipeline;
        targets.ClearUse();
        FrameCounts counts = plan.Counts.WithTarget(targets.Width, targets.Height);
        foreach (PipelineStage stage in (ReadOnlySpan<PipelineStage>)[PipelineStage.Scene, PipelineStage.Lighting, PipelineStage.Sky, PipelineStage.Transparency, PipelineStage.Post])
        {
            bool perView = PassState.ScopeOf(stage) == PassScope.View;
            foreach (PassState pass in pipeline.Stages[(int)stage])
            {
                if (!pipeline.Runs(pass, plan.Counts))
                    continue;

                if (!perView)
                {
                    ctx.Resources.Ensure(ctx, pipeline, pass, targets.Resources, targets, counts);
                    continue;
                }

                foreach (ref readonly ViewPlan view in views)
                    ctx.Resources.Ensure(ctx, pipeline, pass, view.Resources, targets, counts.WithView(view.Rect, HiZ.Size(view.Rect.Width, view.Rect.Height).Floats));
            }
        }

        targets.Ensure(ctx.Gpu, targets.Width, targets.Height);
        targets.ReleaseUnused(ctx.Gpu);
    }

    /// <summary>
    /// The occupancy bricks due this frame, as many as every pass that runs once per brick takes (the fewest any of them
    /// iterates), taken off the queue and uploaded with their dispatches; how many there are. None while one of those
    /// passes does not run yet (still compiling, say): a job taken then would be cleared and never built, and its mesh
    /// would have no voxels for good.
    /// </summary>
    private static int UploadBrickJobs(RenderContext ctx)
    {
        int budget = int.MaxValue;
        foreach (PassState pass in ctx.Pipeline.Stages[(int)PipelineStage.Gi])
        {
            if (pass.Pass.Each.Over != EachOver.Bricks)
                continue;
            if (!pass.Ready || ctx.Pipeline.Unfit.Contains(pass.Id))
                return 0;

            budget = Math.Min(budget, (int)pass.Pass.Each.Max);
        }

        if (budget == int.MaxValue || ctx.Bricks.Pending.Count == 0)
            return 0;

        Span<GpuBrickJob> jobs = stackalloc GpuBrickJob[PassValidator.MaxIterations];
        Span<UintVector4> dispatches = stackalloc UintVector4[PassValidator.MaxIterations];
        int count = ctx.Bricks.Jobs(ctx.Meshes, Math.Min(budget, PassValidator.MaxIterations), jobs, dispatches);
        if (count == 0)
            return 0;

        ctx.BrickJobs.Upload<GpuBrickJob>(ctx.Gpu, jobs[..count]);
        ctx.BrickArgs.Upload<UintVector4>(ctx.Gpu, dispatches[..count]);
        Debugging.Stats.Set("Rendering.Gi.Bricks", ctx.Bricks.Count);
        Debugging.Stats.Set("Rendering.Gi.BricksPending", ctx.Bricks.Pending.Count);
        Debugging.Stats.Set("Rendering.Gi.BricksDropped", ctx.Bricks.Dropped);
        return count;
    }

    /// <summary>
    /// What the frame-wide stages' passes create is made ready in the frame's set.
    /// </summary>
    private static void PrepareFrameStages(RenderContext ctx, FramePlan plan)
    {
        PipelineState pipeline = ctx.Pipeline;
        foreach (PipelineStage stage in (ReadOnlySpan<PipelineStage>)[PipelineStage.Shadows, PipelineStage.Gi])
        {
            foreach (PassState pass in pipeline.Stages[(int)stage])
            {
                if (pipeline.Runs(pass, plan.Counts))
                    ctx.Resources.Ensure(ctx, pipeline, pass, ctx.Resources.Frame, null, plan.Counts);
            }
        }
    }

    private static bool IsPlanned(FramePlan plan, RenderTarget target)
    {
        foreach (TargetPlan planned in plan.Targets)
        {
            if (planned.FrameTargets.RenderTarget == target)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The window's targets at the settings' resolution (the window's own size without one); null when it is not open (warned once, its targets released) or minimised.
    /// </summary>
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

        // Rendered at the render scale's share of the window, in its shape; Present stretches the result over it.
        float scale = ctx.Deferred.Scale;
        size = new Size(Math.Clamp((int)MathF.Round(size.Width * scale), 1, MaxResolution), Math.Clamp((int)MathF.Round(size.Height * scale), 1, MaxResolution));

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

    /// <summary>
    /// Window 0 is the main window.
    /// </summary>
    private static RenderTarget Resolve(RenderTarget target, uint main)
    {
        return !target.IsTexture && target.Window == 0 ? RenderTarget.Of(main) : target;
    }

    /// <summary>
    /// One render target: the scene stage culls and draws its views into its gbuffer, the lighting stage lights each view
    /// into Hdr, the sky, transparency and post stages run, what failed is shown, and the result is presented. Returns
    /// the draws and dispatches recorded.
    /// </summary>
    private static (int Draws, int Dispatches) RecordTarget(
        RenderContext ctx,
        RenderCommands commands,
        FramePlan plan,
        FrameTargets targets,
        ReadOnlySpan<ViewPlan> views)
    {
        int draws = 0, dispatches = 0;
        Vector4 clearColor = plan.ClearColor;

        // The scene stage: cull, draw into the gbuffer, with occlusion build the pyramid and draw what was held back.
        PassExecutor.RecordTarget(ctx, commands, plan, targets, views, PipelineStage.Scene, ref draws, ref dispatches);

        // The lighting writes every pixel of a view into Hdr, and nothing else of it: when the views leave part of
        // the target uncovered, that part is cleared first.
        if (views.Length != 1 || views[0].Rect != new Rectangle(0, 0, (int)targets.Width, (int)targets.Height))
        {
            commands.BeginRenderPass(targets.Hdr.Texture, GpuLoad.Clear, clearColor: clearColor);
            commands.EndRenderPass();
        }

        PassExecutor.RecordTarget(ctx, commands, plan, targets, views, PipelineStage.Lighting, ref draws, ref dispatches);

        // Ldr is what is shown and what a screenshot reads. A tonemap post writes it; when no listed post did (none
        // listed, or it is still compiling or broken) the linear scene is copied into it, so neither is ever stale.
        PassExecutor.RecordTarget(ctx, commands, plan, targets, views, PipelineStage.Sky, ref draws, ref dispatches);
        PassExecutor.RecordTarget(ctx, commands, plan, targets, views, PipelineStage.Transparency, ref draws, ref dispatches);
        bool wroteLdr = PassExecutor.RecordTarget(ctx, commands, plan, targets, views, PipelineStage.Post, ref draws, ref dispatches);
        if (!wroteLdr)
            commands.Blit(targets.Hdr.Texture, new Rectangle(0, 0, (int)targets.Hdr.Width, (int)targets.Hdr.Height), new TextureRegion(targets.Ldr.Texture));

        draws += FailureOverlay.Record(ctx, commands, targets, views[0].Constants);
        Present(ctx, commands, targets);

        return (draws, dispatches);
    }

    /// <summary>
    /// The finished frame, Ldr, to where it is shown: a window's swapchain image, or every mip level of the render
    /// texture's pool layer, each straight from Ldr (so no level is read while it is written). A render texture of
    /// another shape is stretched into the square layer, and back again on whatever samples it.
    /// </summary>
    private static void Present(RenderContext ctx, RenderCommands commands, FrameTargets targets)
    {
        FrameTargets.Target shown = targets.Ldr;
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
}
