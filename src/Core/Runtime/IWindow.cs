using Magic.Assets;
using Magic.Assets.Types;

namespace Magic.Runtime;

/// <summary>
/// The window the game draws into, which a platform gem opens. Its title, icon and size are the main thread's, and a new
/// title or icon reaches the OS once the gem gets to it.
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
    /// What the platform knows the window by, 0 when there is none.
    /// </summary>
    uint Id { get; }
}
