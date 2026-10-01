using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Systems.DebugUI;

/// <summary>
/// One debug UI window as a system: its <see cref="Key"/> opens and closes it, and while it is <see cref="Open"/> the
/// debug UI shows (<see cref="Debugging.UI.Visible"/>), from the moment it opens, so it draws the same frame. Input is
/// handed in every frame: it lives in a gem. The frame loop calls these after every gem's Update.
/// </summary>
public abstract class DebugWindowSystem : IDisposable
{
    protected DebugWindowSystem(Key key)
    {
        Key = key;
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

    /// <summary>Toggles the window on its key, then runs it: <see cref="Run"/> is called every frame, open or not.</summary>
    public void Update(in Frame frame, IInput? input)
    {
        if (input?.WasPressed(Key) == true)
            Open = !Open;

        Run(frame);
    }

    public virtual void Dispose()
    {
        Open = false;
    }

    /// <summary>The window's frame: draw it while <see cref="Open"/>, and whatever else it does every frame.</summary>
    protected abstract void Run(in Frame frame);
}
