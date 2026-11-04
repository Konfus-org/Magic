using System.Collections.Immutable;
using Magic.Files;
using Magic.Files.Types;
using Magic.Utils;

namespace Magic.Platform;

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

    /// <summary>
    /// The Vulkan instance extensions a surface on this window needs; none headless, or when the platform has no
    /// Vulkan.
    /// </summary>
    ImmutableArray<string> VulkanExtensions { get; }

    /// <summary>
    /// A Vulkan surface on the window for <paramref name="instance"/>, a <c>VkInstance</c>, as a <c>VkSurfaceKHR</c>
    /// the caller destroys; why not when the window has none to give. Main thread.
    /// </summary>
    Result<ulong> CreateVulkanSurface(nint instance);
}
