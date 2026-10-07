using Magic.Contexts;
using Magic.Contexts.Assets;
using System.Drawing;

namespace Magic.Interfaces;

/// <summary>
/// How a window takes its display: <see cref="Windowed"/> framed, <see cref="Fullscreen"/> all of it,
/// <see cref="Borderless"/> at its own size without a frame.
/// </summary>
public enum WindowMode
{
    Windowed,
    Fullscreen,
    Borderless
}

/// <summary>
/// A window on the desktop, opened by the windowing gem and found again by its <see cref="IWindow.Handle"/> through
/// <see cref="IWindowRegistry"/>. Once closed, by the user say, <see cref="IsOpen"/> is false; the windowing gem destroys
/// it as it unloads.
/// </summary>
public interface IWindow
{
    bool IsOpen { get; }

    /// <summary>
    /// The window's id, as the windowing backend reports it in its events.
    /// </summary>
    uint Handle { get; }

    string Title { get; set; }

    Handle<Texture> Icon { get; set; }

    /// <summary>
    /// The size in points, what positions in input events are measured in.
    /// </summary>
    Size Size { get; set; }

    /// <summary>
    /// The drawable size in pixels: <see cref="Size"/> times the display scale.
    /// </summary>
    Size PixelSize { get; }

    WindowMode Mode { get; set; }

    void Show();

    void Hide();

    void Minimize();

    void Maximize();
}
