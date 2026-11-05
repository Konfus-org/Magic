using Magic.Debugging;
using Magic.Input;

namespace Magic.GIRendering;

/// <summary>
/// The renderer's debug window, drawn with the debug UI's widgets: it takes the view chosen now and hands back the one
/// chosen next. F2 opens it, F5 and F6 step through the views, and a line on screen names any view but the finished frame.
/// </summary>
internal sealed class RendererDebugWindow(IDebugUI ui, IInput? input)
{
    private static readonly string[] _viewNames = Enum.GetNames<DebugView>();

    /// <summary>
    /// The line naming a view, made once per view.
    /// </summary>
    private static readonly string[] _viewLines = Array.ConvertAll(_viewNames, name => $"View: {name}");

    private bool _isOpen;

    public bool IsOpen
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    public DebugView Update(DebugView current)
    {
        DebugView view = current;
        if (input is not null)
        {
            if (input[Control.KeyF2].WasPressed)
                _isOpen = !_isOpen;

            if (input[Control.KeyF5].WasPressed)
                view = Step(view, -1);

            if (input[Control.KeyF6].WasPressed)
                view = Step(view, 1);
        }

        if (_isOpen)
            view = Draw(view);

        if (view != DebugView.Final)
            ui.Text(_viewLines[(int)view]);

        return view;
    }

    private static DebugView Step(DebugView view, int by) =>
        (DebugView)(((int)view + by + _viewNames.Length) % _viewNames.Length);

    private DebugView Draw(DebugView current)
    {
        ui.Begin("Rendering", ref _isOpen);
        int viewIndex = (int)current;
        ui.Choice("View", ref viewIndex, _viewNames);
        ui.End();

        return (DebugView)viewIndex;
    }
}
