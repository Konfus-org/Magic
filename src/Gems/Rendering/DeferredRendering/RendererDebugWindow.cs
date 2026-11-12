using Magic.Debugging;
using Magic.Input;

namespace Magic.DeferredRendering;

/// <summary>
/// The renderer's debug window, which writes the view and whether it is open, as chosen this frame, into the settings.
/// </summary>
/// <remarks>
/// Its key (F2) opens it, two more (F5 and F6) step through the views, and a line on screen names any view but the
/// finished frame.
/// </remarks>
internal sealed class RendererDebugWindow(IDebugUI ui, IInput? input)
{
    private static readonly string[] _viewNames = Enum.GetNames<DebugView>();

    /// <summary>
    /// The line naming a view, made once per view.
    /// </summary>
    private static readonly string[] _viewLines = Array.ConvertAll(_viewNames, name => $"View: {name}");

    /// <summary>
    /// Draws the window and writes the view and whether the window is open, as chosen this frame, into
    /// <paramref name="current"/>.
    /// </summary>
    public void Update(DeferredRenderingSettings current)
    {
        DebugView view = current.RenderView;
        bool isOpen = current.IsRenderWindowOpen;
        if (input is not null)
        {
            if (input[current.RenderWindowKey].WasPressed)
                isOpen = !isOpen;

            if (input[current.PreviousViewKey].WasPressed)
                view = Step(view, -1);

            if (input[current.NextViewKey].WasPressed)
                view = Step(view, 1);
        }

        if (isOpen)
        {
            int viewIndex = (int)view;
            ui.Begin("Rendering", ref isOpen);
            ui.Choice("View", ref viewIndex, _viewNames);
            ui.End();
            view = (DebugView)viewIndex;
        }

        if (view != DebugView.Final)
            ui.Text(_viewLines[(int)view]);

        current.RenderView = view;
        current.IsRenderWindowOpen = isOpen;
    }

    private static DebugView Step(DebugView view, int by) =>
        (DebugView)(((int)view + by + _viewNames.Length) % _viewNames.Length);
}
