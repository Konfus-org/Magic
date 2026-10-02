using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Systems.DebugUI;

/// <summary>
/// One debug UI window as a system: its <see cref="Key"/> opens and closes it, and while it is <see cref="Open"/> the
/// debug UI shows (<see cref="Debugging.UI.Visible"/>), from the moment it opens, so it draws the same frame. Without
/// an <see cref="IInput"/> (no input gem is loaded) its key does nothing.
/// </summary>
internal abstract class DebugWindowSystem : ISystem
{
    private readonly IInput? _input;
    private readonly string _title;

    private bool _visible;

    protected DebugWindowSystem(string name, Key key, IInput? input)
    {
        Key = key;
        _input = input;
        _title = $"{name} ({(key == Key.Grave ? "`" : key.ToString())} to Toggle)";
    }

    public virtual void Dispose()
    {
        Open = false;
    }

    public Key Key { get; }

    public bool Open
    {
        get;
        set
        {
            field = value;
            Debugging.UI.Show(this, value);
        }
    }

    /// <summary>Toggles the window on its key, then draws it: <see cref="Draw"/> is called every frame, open or not.</summary>
    public void Run(in Frame frame)
    {
        if (_input?.WasPressed(Key) == true)
            Open = !Open;

        _visible = Open;
        Draw(frame);
        Open = _visible; // closed by its button: only now, so the frame's End still reached the UI
    }

    /// <summary>Opens the window, titled with its name and key, with a close button. Pair with <see cref="Debugging.UI.End"/>.</summary>
    protected void Begin(bool scrollable = false)
    {
        Debugging.UI.Begin(_title, ref _visible, scrollable);
    }

    /// <summary>The window's frame: draw it while <see cref="Open"/>, and whatever else it does every frame.</summary>
    protected abstract void Draw(in Frame frame);
}
