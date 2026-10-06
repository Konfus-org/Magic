using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Utils;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// Keeps <see cref="PipelineState"/> in step with what the world asks for, once a frame before it is planned: the
/// pipeline asset the world names is loaded (and loaded again when it changes), every pass it and the post list name is
/// in the table with its load started, every pass no longer named is unloaded with everything it made, the stage lists
/// are built in order, waiting pipelines are finished, and the listing is held together (<see cref="PassValidator.Fit"/>).
/// </summary>
internal static class PipelineSync
{
    public static void Sync(RenderContext ctx, Handle<Pipeline> pipeline, PostList posts)
    {
        PipelineState state = ctx.Pipeline;
        if (state.Current != pipeline)
        {
            state.Current = pipeline;
            state.Asset = null;
            state.Error = null;
            if (pipeline.IsValid)
                ctx.Reloads.Start(pipeline.Id, cancel => Preloads.PipelineAsync(ctx.Assets, pipeline.Id, cancel));
            else
                state.Problem("No pipeline is named: nothing but the window's clear is drawn. Set \"pipeline\" in the .magic file or pass --pipeline.");
        }

        List<(ulong Id, PipelineStage Stage)> wanted = state.Wanted;
        Wanted(state, posts, wanted);
        if (!CollectionsMarshal.AsSpan(wanted).SequenceEqual(CollectionsMarshal.AsSpan(state.Listed)))
            Relist(ctx, state, wanted);

        foreach ((ulong id, _) in state.Listed)
            PassLoader.Finish(ctx, id);

        // The listing is held together once every pass has been read, and again whenever it changes: what a pass still
        // loading will make is unknown until then, and a pass waiting for it just waits.
        if (!state.Changed)
            return;

        state.Unfit.Clear();
        if (!AllRead(state))
            return;

        state.Changed = false;
        foreach ((PassState pass, string problem) in PassValidator.Fit(ctx, state))
        {
            state.Unfit.Add(pass.Id);
            state.Problem($"Pass {pass.Path} does not fit the pipeline and is skipped: it {problem}");
        }
    }

    /// <summary>
    /// The listing changed: a reference is taken on every pass now listed before the last listing's are dropped (so a
    /// pass listed in both is kept), and the stage lists are built again.
    /// </summary>
    private static void Relist(RenderContext ctx, PipelineState state, List<(ulong Id, PipelineStage Stage)> wanted)
    {
        state.Changed = true;
        Acquire(ctx, state, wanted);
        ReleaseUnlisted(ctx, state);
        state.Listed.Clear();
        state.Listed.AddRange(wanted);

        foreach (List<PassState> stage in state.Stages)
            stage.Clear();
        foreach ((ulong id, PipelineStage stage) in wanted)
        {
            if (state.Passes.TryGet(id, out PassState pass) && pass.Stage == stage && !state.Stages[(int)stage].Contains(pass))
                state.Stages[(int)stage].Add(pass);
        }
    }

    private static bool AllRead(PipelineState state)
    {
        foreach ((ulong id, _) in state.Listed)
        {
            if (state.Passes.TryGet(id, out PassState pass) && pass.Path.Length == 0 && pass.Error is null)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The pipeline asset arrived (or did not): what it lists is taken up at the next <see cref="Sync"/>.
    /// </summary>
    public static void Loaded(RenderContext ctx)
    {
        PipelineState state = ctx.Pipeline;
        state.Asset = Preloads.Get<Pipeline>(ctx, state.Current.Id);
        if (state.Asset is null)
            state.Problem($"Pipeline {ctx.Assets.PathOf(state.Current.Id) ?? state.Current.Id.ToString()} could not be loaded: nothing but the window's clear and the post list is drawn.");
        else
            Debugging.Log.Info($"Pipeline {state.Asset.Path} loaded.");
    }

    private static void Wanted(PipelineState state, PostList posts, List<(ulong Id, PipelineStage Stage)> wanted)
    {
        wanted.Clear();
        if (state.Asset is { } asset)
        {
            for (PipelineStage stage = PipelineStage.Shadows; stage < PipelineStage.Post; stage++)
            {
                foreach (Handle<Pass> handle in asset.StageOf(stage))
                {
                    if (handle.IsValid)
                        wanted.Add((handle.Id, stage));
                }
            }
        }

        ReadOnlySpan<Handle<Post>> listed = posts;
        foreach (Handle<Post> handle in listed[..posts.Count])
            wanted.Add((handle.Id, PipelineStage.Post));
    }

    /// <summary>
    /// Every wanted pass is in the table: a reference taken on one there (it keeps its stage), a load started for a new one.
    /// </summary>
    private static void Acquire(RenderContext ctx, PipelineState state, List<(ulong Id, PipelineStage Stage)> wanted)
    {
        foreach ((ulong id, PipelineStage stage) in wanted)
        {
            if (state.Passes.TryAcquire(id, out PassState? existing))
            {
                if (existing.Stage != stage)
                    state.Problem($"Pass {ctx.Assets.PathOf(id) ?? id.ToString()} is listed in {existing.Stage} and in {stage}; it runs where it was listed first.");
                continue;
            }

            // Not ready, so not run, until its file and its shaders have arrived: the render system loads it then.
            state.Passes.Add(id, new PassState(id, stage));
            ctx.Reloads.Start(id, cancel => Preloads.PassAsync(ctx.Assets, id, cancel));
        }
    }

    /// <summary>
    /// The references the last sync held are dropped: a pass nobody lists any more goes, with its pipeline, its compiles,
    /// everything it made and (when no other pass shares it) its shader's text.
    /// </summary>
    private static void ReleaseUnlisted(RenderContext ctx, PipelineState state)
    {
        foreach ((ulong id, _) in state.Listed)
        {
            if (!state.Passes.Release(id, out PassState? gone))
                continue;

            ctx.Gpu.Release(gone.Pipeline);
            state.Compiles.Remove(id);
            state.Compiles.Remove(id | PipelineState.FragmentKeyBit);
            ctx.Reloads.Remove(id);
            ctx.Resources.ReleaseOwnedBy(ctx, id);
            foreach (ulong shader in (ReadOnlySpan<ulong>)[gone.ShaderId, gone.FragmentId])
            {
                if (shader != 0 && !state.Passes.Entries.Any(entry => entry.Value.ShaderId == shader || entry.Value.FragmentId == shader))
                    ctx.Shaders.Entries.Remove(shader);
            }

            Debugging.Log.Verbose($"Pass {gone.Path} unloaded: it is no longer listed.");
        }
    }
}
