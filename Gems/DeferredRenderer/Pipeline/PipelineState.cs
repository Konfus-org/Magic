using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// The pipeline the frame follows: which <see cref="Pipeline"/> asset, loaded or not, and every pass it and the
/// world's post list name, id to <see cref="PassState"/>, reference counted by the listing, with their compiles, the
/// stage lists built from them each frame, and the pipelines every pass is built on (the fullscreen vertex shader) or
/// shown with when it fails (the failure overlay).
/// </summary>
internal sealed class PipelineState(CompiledShader fullscreenVertex, GpuPipeline failureOverlay)
{
    /// <summary>
    /// A fragment shader compiles under its pass's id with this bit set, beside the pass's own compile.
    /// </summary>
    public const ulong FragmentKeyBit = 1UL << 63;

    public Handle<Pipeline> Current { get; set; }

    /// <summary>
    /// The pipeline asset as loaded; null until it arrives, or when it could not be read (then <see cref="Error"/>).
    /// </summary>
    public Pipeline? Asset { get; set; }

    public string? Error { get; set; }

    public RefCountTable<ulong, PassState> Passes { get; } = new();

    /// <summary>
    /// The ids the pipeline and the post list named when they were last synced: one reference each.
    /// </summary>
    public List<ulong> Held { get; } = [];

    /// <summary>
    /// What this frame's sync wants listed, with the stage of each; kept between frames so nothing is allocated.
    /// </summary>
    public List<(ulong Id, PipelineStage Stage)> Wanted { get; } = [];

    /// <summary>
    /// The fit check's working set: every name that stands for something so far, and what does not fit.
    /// </summary>
    public Dictionary<string, ResourceInfo> Made { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(PassState Pass, string Problem)> UnfitFound { get; } = [];

    /// <summary>
    /// Set when the listing changed (a pass listed, unlisted, loaded, compiled or disabled): the fit check runs again.
    /// </summary>
    public bool Changed { get; set; } = true;

    public Pending<ulong, Result<CompiledShader>> Compiles { get; } = new();

    public CompiledShader FullscreenVertex { get; } = fullscreenVertex;

    public GpuPipeline FailureOverlay { get; } = failureOverlay;

    /// <summary>
    /// The passes of each stage, in order, as listed this frame, ready or not; indexed by <see cref="PipelineStage"/>.
    /// </summary>
    public List<PassState>[] Stages { get; } = [.. Enum.GetValues<PipelineStage>().Select(_ => new List<PassState>())];

    /// <summary>
    /// The passes that do not fit the listing this frame (a name nothing made by then, a write to what they may not
    /// write), by id: they are skipped, and shown as failures.
    /// </summary>
    public HashSet<ulong> Unfit { get; } = [];

    /// <summary>
    /// What was logged once about the listing, so it is not logged again while it holds.
    /// </summary>
    public HashSet<string> Problems { get; } = [];

    /// <summary>
    /// Says <paramref name="problem"/> once: logged as an error the first time it comes up, kept while it holds.
    /// </summary>
    public void Problem(string problem)
    {
        if (Problems.Add(problem))
            Debugging.Log.Error(problem);
    }

    /// <summary>
    /// Notes <paramref name="note"/> once, verbosely: what is worth a line while developing and nothing more.
    /// </summary>
    public void Note(string note)
    {
        if (Problems.Add(note))
            Debugging.Log.Verbose(note);
    }

    /// <summary>
    /// Whether <paramref name="pass"/> runs this frame: it is ready, it fits, and the count it needs (if any) is not 0.
    /// </summary>
    public bool Runs(PassState pass, in FrameCounts counts)
    {
        return pass.Ready && !Unfit.Contains(pass.Id) && (pass.Pass.Needs.Length == 0 || (counts.TryResolve(pass.Pass.Needs, out uint needed) && needed > 0));
    }
}
