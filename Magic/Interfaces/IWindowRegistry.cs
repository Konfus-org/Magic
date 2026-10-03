namespace Magic.Interfaces;

/// <summary>
/// The windows that are open right now, exported by the windowing gem next to <see cref="IWindowFactory"/>.
/// A renderer resolves the window a camera targets through this, by the handle the camera's render target
/// carries: it never receives a window from the host or from an ECS singleton.
/// </summary>
public interface IWindowRegistry
{
    /// <summary>
    /// The first window opened and still open, or null before it exists or after it closed.
    /// </summary>
    IWindow? Main { get; }

    /// <summary>
    /// Every open window, in creation order. A snapshot: safe to iterate while windows close.
    /// </summary>
    IReadOnlyList<IWindow> Windows { get; }

    /// <summary>
    /// The window with this <see cref="IWindow.Handle"/>, or null if it is not open.
    /// </summary>
    IWindow? Get(uint handle);
}
