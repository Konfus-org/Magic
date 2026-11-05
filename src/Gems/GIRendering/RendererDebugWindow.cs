using Magic.Debugging;
using Magic.Input;

namespace Magic.GIRendering;

/// <summary>
/// What someone debugging can choose of how the frame is drawn.
/// </summary>
internal readonly record struct RendererChoices(DebugView View, RendererPreset Preset, bool WantsRayQuery);

/// <summary>
/// The renderer's debug window, drawn with the debug UI's widgets: it takes what is chosen now and hands back what is
/// chosen next. F2 opens it, F5 and F6 step through the views, and a line on screen names any view but the finished frame.
/// </summary>
internal sealed class RendererDebugWindow(IDebugUI ui, IInput? input, bool canTraceRays)
{
    private static readonly string[] _viewNames = Enum.GetNames<DebugView>();
    private static readonly string[] _presetNames = CreatePresetNames();
    private static readonly string[] _tracerNames = ["Field", "Ray query"];

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

    public RendererChoices Update(in RendererChoices current)
    {
        DebugView view = current.View;
        if (input is not null)
        {
            if (input[Control.KeyF2].WasPressed)
                _isOpen = !_isOpen;

            if (input[Control.KeyF5].WasPressed)
                view = Step(view, -1);

            if (input[Control.KeyF6].WasPressed)
                view = Step(view, 1);
        }

        RendererChoices chosen = current with { View = view };
        if (_isOpen)
            chosen = Draw(chosen);

        if (chosen.View != DebugView.Final)
            ui.Text(_viewLines[(int)chosen.View]);

        return chosen;
    }

    private static DebugView Step(DebugView view, int by) =>
        (DebugView)(((int)view + by + _viewNames.Length) % _viewNames.Length);

    private static string[] CreatePresetNames()
    {
        string[] names = new string[RendererPreset.All.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = RendererPreset.All[i].Name;
        }

        return names;
    }

    private RendererChoices Draw(in RendererChoices current)
    {
        ui.Begin("Rendering", ref _isOpen);
        int viewIndex = (int)current.View;
        ui.Choice("View", ref viewIndex, _viewNames);
        int presetIndex = RendererPreset.All.IndexOf(current.Preset);
        ui.Choice("Preset", ref presetIndex, _presetNames);
        int tracerIndex = current.WantsRayQuery ? 1 : 0;
        if (canTraceRays)
            ui.Choice("Tracer", ref tracerIndex, _tracerNames);

        ui.End();

        return new RendererChoices((DebugView)viewIndex, RendererPreset.All[presetIndex], tracerIndex == 1);
    }
}
