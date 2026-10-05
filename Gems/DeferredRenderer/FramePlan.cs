using Magic.Contexts.Rendering;
using System.Drawing;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// One view drawn into one render target this frame: which view, its rectangle in the target, what the view-stage
/// passes made for it, and its constants.
/// </summary>
internal readonly record struct ViewPlan(int Index, Rectangle Rect, ResourceSet Resources, FrameConstants Constants);

/// <summary>
/// One render target (window or render texture) drawn this frame: its textures and its run of <see cref="ViewPlan"/>s.
/// </summary>
internal readonly record struct TargetPlan(FrameTargets FrameTargets, int FirstView, int ViewCount);

/// <summary>
/// What one frame draws, decided before anything is recorded: every render target with its views next to each other,
/// render textures first and then windows, the counts the passes are sized by, the view the frame-wide passes are
/// planned from, and the main window when no camera draws into it (it is cleared). Planning fills it; recording only
/// reads it.
/// </summary>
internal sealed class FramePlan
{
    public List<TargetPlan> Targets { get; } = [];

    public List<ViewPlan> Views { get; } = [];

    /// <summary>
    /// The index in <see cref="Views"/> of the view the shadows and the GI are planned around: the first perspective
    /// camera of the first target, else the first view.
    /// </summary>
    public int MainView { get; set; }

    /// <summary>
    /// What the tables hold this frame, for sizing what the passes create.
    /// </summary>
    public FrameCounts Counts { get; set; }

    /// <summary>
    /// The main window to clear because no view draws into it; 0 for none.
    /// </summary>
    public uint ClearWindow { get; set; }

    /// <summary>
    /// What shows where nothing is drawn: the sky's colour when a Sky entity set one, else the fixed clear colour.
    /// </summary>
    public Vector4 ClearColor { get; set; } = RenderCommands.ClearColor;

    public void Clear()
    {
        Targets.Clear();
        Views.Clear();
        MainView = 0;
        ClearWindow = 0;
    }
}
