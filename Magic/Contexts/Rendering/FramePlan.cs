using System.Drawing;

namespace Magic.Contexts.Rendering;

/// <summary>
/// One view drawn into one render target this frame: which view, its rectangle in the target, its buffers and its constants.
/// </summary>
internal readonly record struct ViewPlan(int Index, Rectangle Rect, ViewBuffers Buffers, FrameConstants Constants);

/// <summary>
/// One render target (window or render texture) drawn this frame: its textures, its run of <see cref="ViewPlan"/>s and
/// its run of <see cref="FramePlan.Passes"/>.
/// </summary>
internal readonly record struct TargetPlan(FrameTargets FrameTargets, int FirstView, int ViewCount, int FirstPass, int PassCount);

/// <summary>
/// What one frame draws, decided before anything is recorded: every render target with its views next to each other,
/// render textures first and then windows, each with the passes that run over it in order, and the main window when no
/// camera draws into it (it is cleared). Planning fills it; recording only reads it.
/// </summary>
internal sealed class FramePlan
{
    public List<TargetPlan> Targets { get; } = [];

    public List<ViewPlan> Views { get; } = [];

    /// <summary>
    /// The passes that run this frame, each target's next to each other in the order they run.
    /// </summary>
    public List<PassState> Passes { get; } = [];

    /// <summary>
    /// The main window to clear because no view draws into it; 0 for none.
    /// </summary>
    public uint ClearWindow { get; set; }

    public void Clear()
    {
        Targets.Clear();
        Views.Clear();
        Passes.Clear();
        ClearWindow = 0;
    }
}
