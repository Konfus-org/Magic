using Magic.Assets;
using Magic.Assets.Types;

namespace Magic.Host;


/// <summary>
/// The window the game draws into, which a platform gem opens on the render thread, where the OS keeps it. Its title,
/// icon, size and clipboard are the main thread's: the size and clipboard as of the provider's Update this frame, and a
/// new title, icon or clipboard reaches the OS once the render thread gets to it.
/// </summary>
public interface IWindow
{
    string Title { get; set; }

    /// <summary>
    /// What the window shows in its title bar and task bar until
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
    /// TODO(Konfus): remove this, make a IClipboard interface for clipboard related things.
    /// The text on the OS's clipboard, which copy and paste go through; empty when there is none, as in a headless run.
    /// </summary>
    string Clipboard { get; set; }

    /// <summary>
    /// What the platform knows the window by, 0 when there is none.
    /// </summary>
    uint Id { get; }
}
