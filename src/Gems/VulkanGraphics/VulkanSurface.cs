using System.Collections.Immutable;
using Magic.Platform;
using Magic.Utils;
using Vortice.Vulkan;

namespace VulkanGraphics;

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
        VkResult result = handle.System switch
        {
            WindowSystem.Windows => CreateOnWindows(api, handle, out surface),
            WindowSystem.X11 => CreateOnX11(api, handle, out surface),
            WindowSystem.Wayland => CreateOnWayland(api, handle, out surface),
            WindowSystem.Cocoa => CreateOnCocoa(api, handle, out surface),
            WindowSystem.None => VkResult.ErrorInitializationFailed,
            _ => VkResult.ErrorInitializationFailed,
        };

        return result == VkResult.Success
            ? Result.Success(surface)
            : Result.Failure($"a {handle.System} window gave no surface: {result}.");
    }

    private static VkResult CreateOnWindows(VkInstanceApi api, WindowHandle handle, out VkSurfaceKHR surface)
    {
        VkWin32SurfaceCreateInfoKHR info = new() { hinstance = handle.Display, hwnd = handle.Window };

        return api.vkCreateWin32SurfaceKHR(&info, out surface);
    }

    private static VkResult CreateOnX11(VkInstanceApi api, WindowHandle handle, out VkSurfaceKHR surface)
    {
        VkXlibSurfaceCreateInfoKHR info = new() { dpy = handle.Display, window = (ulong)handle.Window };

        return api.vkCreateXlibSurfaceKHR(&info, out surface);
    }

    private static VkResult CreateOnWayland(VkInstanceApi api, WindowHandle handle, out VkSurfaceKHR surface)
    {
        VkWaylandSurfaceCreateInfoKHR info = new() { display = handle.Display, surface = handle.Window };

        return api.vkCreateWaylandSurfaceKHR(&info, out surface);
    }

    private static VkResult CreateOnCocoa(VkInstanceApi api, WindowHandle handle, out VkSurfaceKHR surface)
    {
        VkMetalSurfaceCreateInfoEXT info = new() { pLayer = handle.Window };

        return api.vkCreateMetalSurfaceEXT(&info, out surface);
    }
}
