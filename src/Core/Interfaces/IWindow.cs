using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// The window the game draws into, which a platform gem opens on the render thread, where the OS keeps it. Its title,
/// icon, size and clipboard are the main thread's: the size and clipboard as of the provider's Update this frame, and a
/// new title, icon or clipboard reaches the OS once the render thread gets to it.
/// </summary>
public interface IWindow
{
    string Title { get; set; }

    /// <summary>
    /// What the window shows in its title bar and task bar: the default icon (<see cref="Assets.Default{T}"/>) until
    /// it is set. A new one shows once it has loaded.
    /// </summary>
    Handle<Icon> Icon { get; set; }

    /// <summary>
    /// The width of the area drawn into, in pixels.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// The height of the area drawn into, in pixels.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// The text on the OS's clipboard, which copy and paste go through; empty when there is none, as in a headless run.
    /// </summary>
    string Clipboard { get; set; }

    /// <summary>
    /// What the platform knows the window by, 0 when there is none, as in a headless run: a GPU gem that shares the
    /// platform's native library finds the window by it (IGpu.Target).
    /// </summary>
    uint Id { get; }
}
