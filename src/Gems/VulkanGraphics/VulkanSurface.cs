using System.Collections.Immutable;
using Magic.Platform;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// A window's Vulkan surface, made from its OS handles: the one place Vulkan meets each window system.
/// </summary>
internal static unsafe class VulkanSurface
{
    private const string SurfaceExtension = "VK_KHR_surface";

    /// <summary>
    /// The instance extensions a surface on <paramref name="system"/> needs.
    /// </summary>
    public static ImmutableArray<string> GetExtensions(WindowSystem system) =>
        system switch
        {
            WindowSystem.None => [],
            WindowSystem.Windows => [SurfaceExtension, "VK_KHR_win32_surface"],
            WindowSystem.X11 => [SurfaceExtension, "VK_KHR_xlib_surface"],
            WindowSystem.Wayland => [SurfaceExtension, "VK_KHR_wayland_surface"],
            WindowSystem.Cocoa => [SurfaceExtension, "VK_EXT_metal_surface"],
            _ => throw new ArgumentOutOfRangeException(nameof(system), system, "Vulkan knows no such window system."),
        };

    /// <summary>
    /// A surface on the window <paramref name="handle"/> names, for the caller to destroy; why not when there is none.
    /// </summary>
    public static Result<VkSurfaceKHR> Create(VkInstanceApi api, WindowHandle handle)
    {
        VkSurfaceKHR surface = VkSurfaceKHR.Null;
        VkResult result;
        switch (handle.System)
        {
            case WindowSystem.Windows:
            {
                VkWin32SurfaceCreateInfoKHR info = new() { hinstance = handle.Display, hwnd = handle.Window };
                result = api.vkCreateWin32SurfaceKHR(&info, out surface);
                break;
            }

            case WindowSystem.X11:
            {
                VkXlibSurfaceCreateInfoKHR info = new() { dpy = handle.Display, window = (ulong)handle.Window };
                result = api.vkCreateXlibSurfaceKHR(&info, out surface);
                break;
            }

            case WindowSystem.Wayland:
            {
                VkWaylandSurfaceCreateInfoKHR info = new() { display = handle.Display, surface = handle.Window };
                result = api.vkCreateWaylandSurfaceKHR(&info, out surface);
                break;
            }

            case WindowSystem.Cocoa:
            {
                VkMetalSurfaceCreateInfoEXT info = new() { pLayer = handle.Window };
                result = api.vkCreateMetalSurfaceEXT(&info, out surface);
                break;
            }

            case WindowSystem.None:
            default:
                result = VkResult.ErrorInitializationFailed;
                break;
        }

        return result == VkResult.Success
            ? Result.Success(surface)
            : Result.Failure($"a {handle.System} window gave no surface: {result}.");
    }
}
