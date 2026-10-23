using System.Diagnostics.Metrics;
using Magic.Contexts.Gpu;
using Magic.Extensions;
using Magic.SdlBindings;
using Magic.Services;
using Vortice.Vulkan;
using static Magic.SdlBindings.SDL3;
using VkSurfaceKHR = Vortice.Vulkan.VkSurfaceKHR;

namespace Magic.VulkanGpu;

/// <summary>
/// A window the GPU draws into: its surface, its swapchain's images and what each was last used for, and the
/// semaphores that start a frame's drawing once its image is acquired and present the image once the frame is drawn.
/// </summary>
internal sealed class VulkanWindow(uint id, nint native, VkSurfaceKHR surface)
{
    internal uint Id { get; } = id;

    /// <summary>
    /// SDL's window, the platform gem's.
    /// </summary>
    internal nint Native { get; } = native;

    internal VkSurfaceKHR Surface { get; } = surface;

    internal VkSwapchainKHR Swapchain { get; set; }

    internal VkImage[] Images { get; set; } = [];

    internal VkImageView[] Views { get; set; } = [];

    internal ResourceState[] States { get; set; } = [];

    /// <summary>
    /// Per image: signalled by the frame that drew into it, waited for by presenting it.
    /// </summary>
    internal VkSemaphore[] Drawn { get; set; } = [];

    /// <summary>
    /// Per frame slot: signalled by acquiring an image, waited for by the frame that draws into it.
    /// </summary>
    internal VkSemaphore[] Acquired { get; set; } = [];

    internal GpuFormat Format { get; set; }

    internal VkExtent2D Extent { get; set; }

    /// <summary>
    /// The image this frame draws into, -1 for none yet; and whether this frame asked for one already, as a window with
    /// none (minimised) is not asked again until the next frame.
    /// </summary>
    internal int Image { get; set; } = -1;

    internal bool IsAsked { get; set; }

    /// <summary>
    /// Whether its images can be copied out, as a capture reads them: what the surface lets them be used for says.
    /// </summary>
    internal bool CanCopy { get; set; }

    /// <summary>
    /// The swapchain no longer matches the window, which presenting or acquiring said: it is made again before the next
    /// image is acquired.
    /// </summary>
    internal bool IsStale { get; set; }
}

/// <summary>
/// The windows the GPU draws into, each a swapchain made through SDL's surface for it and made again when the window
/// is resized or presents another way: composed as the settings say where the display can show it, else as SDR, which
/// a warning says; presented on the display's refresh, or as soon as drawn when vsync is off. An image is acquired the
/// first time a frame draws into the window, and presented once the frame is submitted.
/// </summary>
internal sealed unsafe class VulkanWindows(
    VulkanDevice device,
    VulkanGpuSettings settings,
    Histogram<double> acquireWait,
    int framesInFlight) : IDisposable
{
    private readonly VulkanDevice _device = device;
    private readonly VulkanGpuSettings _settings = settings;
    private readonly Histogram<double> _acquireWait = acquireWait;
    private readonly int _framesInFlight = framesInFlight;
    private readonly List<VulkanWindow> _windows = [];

    internal IReadOnlyList<VulkanWindow> Claimed => _windows;

    public void Dispose()
    {
        foreach (VulkanWindow window in _windows)
        {
            Destroy(window);
        }

        _windows.Clear();
    }

    /// <summary>
    /// <paramref name="native"/>, SDL's window number <paramref name="id"/>, as a window the GPU draws into.
    /// </summary>
    internal VulkanWindow Claim(uint id, SDL_Window* native)
    {
        VkSurfaceKHR surface = _device.CreateSurface(native);
        _device.InstanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(
            _device.PhysicalDevice,
            _device.QueueFamily,
            surface,
            out VkBool32 isSupported);
        if (!isSupported)
        {
            _device.InstanceApi.vkDestroySurfaceKHR(surface);

            throw new InvalidOperationException($"The GPU cannot present to window {id}.");
        }

        VulkanWindow window = new(id, (nint)native, surface)
        {
            Acquired = new VkSemaphore[_framesInFlight],
        };
        for (int slot = 0; slot < _framesInFlight; slot++)
        {
            window.Acquired[slot] = CreateSemaphore();
        }

        Create(window);
        _windows.Add(window);

        return window;
    }

    /// <summary>
    /// Destroys a window's swapchain and surface: it was given back, and the GPU is done with it.
    /// </summary>
    internal void Release(VulkanWindow window)
    {
        _device.Api.vkQueueWaitIdle(_device.Queue);
        Destroy(window);
        _windows.Remove(window);
    }

    /// <summary>
    /// Makes every window's swapchain again, the way the settings now say: vsync or the composition changed.
    /// </summary>
    internal void Follow()
    {
        if (_windows.Count == 0) return;

        _device.Api.vkQueueWaitIdle(_device.Queue);
        foreach (VulkanWindow window in _windows)
        {
            Create(window);
        }
    }

    /// <summary>
    /// The image <paramref name="window"/> draws into this frame, acquired the first time the frame asks: false when it
    /// has none, minimised or presented to nothing. What the image was last used for starts over, waited for at the
    /// stage the frame waits for its acquiring at.
    /// </summary>
    internal bool TryAcquire(VulkanWindow window, int slot)
    {
        if (window.IsAsked) return window.Image >= 0;

        window.IsAsked = true;

        if ((SDL_GetWindowFlags((SDL_Window*)window.Native) & SDL_WINDOW_MINIMIZED) != 0) return false;

        if (window.IsStale || HasResized(window)) Create(window);

        if (window.Swapchain.IsNull) return false;

        long startTimestamp = _acquireWait.Start();
        VkResult result = _device.Api.vkAcquireNextImageKHR(
            window.Swapchain,
            ulong.MaxValue,
            window.Acquired[slot],
            VkFence.Null,
            out uint image);
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            Create(window);

            if (window.Swapchain.IsNull) return false;

            result = _device.Api.vkAcquireNextImageKHR(
                window.Swapchain,
                ulong.MaxValue,
                window.Acquired[slot],
                VkFence.Null,
                out image);
        }

        _acquireWait.RecordSince(startTimestamp);
        if (result is not (VkResult.Success or VkResult.SuboptimalKHR))
        {
            Debugging.Warn($"Window {window.Id}'s image was not acquired: {result}");

            return false;
        }

        window.IsStale = result == VkResult.SuboptimalKHR;
        window.Image = (int)image;
        window.States[image] = new ResourceState
        {
            Layout = VkImageLayout.Undefined,
            WriteStages = VkPipelineStageFlags2.ColorAttachmentOutput,
        };

        return true;
    }

    /// <summary>
    /// Presents the image each window drew into this frame, once the frame's drawing signals it, and forgets which it
    /// was.
    /// </summary>
    internal void Present()
    {
        foreach (VulkanWindow window in _windows)
        {
            window.IsAsked = false;

            if (window.Image < 0) continue;

            VkSemaphore drawn = window.Drawn[window.Image];
            VkSwapchainKHR swapchain = window.Swapchain;
            uint image = (uint)window.Image;
            VkPresentInfoKHR info = new()
            {
                waitSemaphoreCount = 1,
                pWaitSemaphores = &drawn,
                swapchainCount = 1,
                pSwapchains = &swapchain,
                pImageIndices = &image,
            };
            VkResult result = _device.Api.vkQueuePresentKHR(_device.Queue, &info);
            if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR) window.IsStale = true;
            else if (result != VkResult.Success) Debugging.Warn($"Window {window.Id} did not present: {result}");

            window.Image = -1;
        }
    }

    /// <summary>
    /// Whether the window's size in pixels is no longer its swapchain's: it was resized, or restored.
    /// </summary>
    private static bool HasResized(VulkanWindow window)
    {
        int width;
        int height;

        if (!SDL_GetWindowSizeInPixels((SDL_Window*)window.Native, &width, &height)) return false;

        return (uint)width != window.Extent.width || (uint)height != window.Extent.height;
    }

    /// <summary>
    /// The surface's own size where it says one, else the window's in pixels, within what the surface allows.
    /// </summary>
    private static VkExtent2D GetExtent(VulkanWindow window, in VkSurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.currentExtent.width != uint.MaxValue) return capabilities.currentExtent;

        int width;
        int height;

        if (!SDL_GetWindowSizeInPixels((SDL_Window*)window.Native, &width, &height)) return default;

        return new VkExtent2D(
            Math.Clamp((uint)width, capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
            Math.Clamp((uint)height, capabilities.minImageExtent.height, capabilities.maxImageExtent.height));
    }

    private static VkSurfaceFormatKHR? FindFormat(
        VkSurfaceFormatKHR[] formats,
        (VkColorSpaceKHR Space, VkFormat First, VkFormat Second) wanted)
    {
        foreach (VkFormat format in (ReadOnlySpan<VkFormat>)[wanted.First, wanted.Second])
        {
            foreach (VkSurfaceFormatKHR offered in formats)
            {
                if (offered.format == format && offered.colorSpace == wanted.Space) return offered;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes the window's swapchain, again where it had one, for its size now and the way the settings say to present;
    /// none while it has no area (minimised).
    /// </summary>
    private void Create(VulkanWindow window)
    {
        _device.InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(
            _device.PhysicalDevice,
            window.Surface,
            out VkSurfaceCapabilitiesKHR capabilities);
        VkExtent2D extent = GetExtent(window, capabilities);
        VkSwapchainKHR old = window.Swapchain;
        if (old.IsNotNull) _device.Api.vkQueueWaitIdle(_device.Queue);

        DestroyImages(window);
        window.IsStale = false;
        window.Extent = extent;
        if (extent.width == 0 || extent.height == 0)
        {
            if (old.IsNotNull) _device.Api.vkDestroySwapchainKHR(old);

            window.Swapchain = VkSwapchainKHR.Null;

            return;
        }

        VkSurfaceFormatKHR format = ChooseFormat(window);
        VkImageUsageFlags usage = VkImageUsageFlags.ColorAttachment
            | (capabilities.supportedUsageFlags & VkImageUsageFlags.TransferSrc);
        uint images = Math.Max(capabilities.minImageCount + 1, 3);
        if (capabilities.maxImageCount > 0) images = Math.Min(images, capabilities.maxImageCount);

        VkSwapchainCreateInfoKHR info = new()
        {
            surface = window.Surface,
            minImageCount = images,
            imageFormat = format.format,
            imageColorSpace = format.colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = usage,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = (capabilities.supportedCompositeAlpha
                & VkCompositeAlphaFlagsKHR.Opaque) != VkCompositeAlphaFlagsKHR.None
                ? VkCompositeAlphaFlagsKHR.Opaque
                : VkCompositeAlphaFlagsKHR.Inherit,
            presentMode = ChoosePresentMode(window),
            clipped = true,
            oldSwapchain = old,
        };
        VkSwapchainKHR swapchain;
        VkResult result = _device.Api.vkCreateSwapchainKHR(&info, &swapchain);
        if (old.IsNotNull) _device.Api.vkDestroySwapchainKHR(old);

        if (result != VkResult.Success)
        {
            Debugging.Warn($"Window {window.Id} has no images: the GPU would not make its swapchain: {result}");
            window.Swapchain = VkSwapchainKHR.Null;

            return;
        }

        window.Swapchain = swapchain;
        window.Format = format.format.ToMagic();
        window.CanCopy = (usage & VkImageUsageFlags.TransferSrc) != VkImageUsageFlags.None;
        CreateImages(window, format.format);
        Debugging.Verbose(
            $"Window {window.Id} presents {window.Format}, {format.colorSpace}, with {info.presentMode}.");
    }

    /// <summary>
    /// The format and colour space the settings' composition asks for, where the surface has them; SDR otherwise, which
    /// is warned about, as every surface has it.
    /// </summary>
    private VkSurfaceFormatKHR ChooseFormat(VulkanWindow window)
    {
        uint count;
        _device.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_device.PhysicalDevice, window.Surface, &count, null);
        VkSurfaceFormatKHR[] formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* first = formats)
        {
            _device.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(
                _device.PhysicalDevice,
                window.Surface,
                &count,
                first);
        }

        Composition asked = _settings.Composition;
        bool canAsk = asked is Composition.Sdr or Composition.SdrLinear || _device.HasColorSpaces;

        if (canAsk && FindFormat(formats, asked.ToVulkan()) is { } found) return found;

        if (asked != Composition.Sdr)
            Debugging.Warn($"Window {window.Id} does not support the {asked} composition: it is composed SDR.");

        return FindFormat(formats, Composition.Sdr.ToVulkan()) ?? formats[0];
    }

    /// <summary>
    /// Vsync, or when the settings turn it off, uncapped: immediate (tearing allowed) where the window has it, else
    /// mailbox, else vsync, which every window has.
    /// </summary>
    private VkPresentModeKHR ChoosePresentMode(VulkanWindow window)
    {
        if (_settings.HasVsync) return VkPresentModeKHR.Fifo;

        uint count;
        _device.InstanceApi
            .vkGetPhysicalDeviceSurfacePresentModesKHR(_device.PhysicalDevice, window.Surface, &count, null);
        VkPresentModeKHR* modes = stackalloc VkPresentModeKHR[(int)count];
        _device.InstanceApi
            .vkGetPhysicalDeviceSurfacePresentModesKHR(_device.PhysicalDevice, window.Surface, &count, modes);
        ReadOnlySpan<VkPresentModeKHR> offered = new(modes, (int)count);

        if (offered.Contains(VkPresentModeKHR.Immediate)) return VkPresentModeKHR.Immediate;

        return offered.Contains(VkPresentModeKHR.Mailbox) ? VkPresentModeKHR.Mailbox : VkPresentModeKHR.Fifo;
    }

    private void CreateImages(VulkanWindow window, VkFormat format)
    {
        uint count;
        _device.Api.vkGetSwapchainImagesKHR(window.Swapchain, &count, null);
        VkImage[] images = new VkImage[count];
        fixed (VkImage* first = images)
        {
            _device.Api.vkGetSwapchainImagesKHR(window.Swapchain, &count, first);
        }

        window.Images = images;
        window.Views = new VkImageView[count];
        window.Drawn = new VkSemaphore[count];
        window.States = new ResourceState[count];
        for (int i = 0; i < count; i++)
        {
            VkImageViewCreateInfo info = new()
            {
                image = images[i],
                viewType = VkImageViewType.Image2D,
                format = format,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
            VkImageView view;
            _device.Api.vkCreateImageView(&info, &view);
            window.Views[i] = view;
            window.Drawn[i] = CreateSemaphore();
        }
    }

    private void DestroyImages(VulkanWindow window)
    {
        foreach (VkImageView view in window.Views)
        {
            _device.Api.vkDestroyImageView(view);
        }

        foreach (VkSemaphore drawn in window.Drawn)
        {
            _device.Api.vkDestroySemaphore(drawn);
        }

        window.Images = [];
        window.Views = [];
        window.Drawn = [];
        window.States = [];
    }

    private void Destroy(VulkanWindow window)
    {
        DestroyImages(window);
        if (window.Swapchain.IsNotNull) _device.Api.vkDestroySwapchainKHR(window.Swapchain);

        foreach (VkSemaphore acquired in window.Acquired)
        {
            _device.Api.vkDestroySemaphore(acquired);
        }

        _device.InstanceApi.vkDestroySurfaceKHR(window.Surface);
    }

    private VkSemaphore CreateSemaphore()
    {
        VkSemaphoreCreateInfo info = new();
        VkSemaphore semaphore;
        _device.Api.vkCreateSemaphore(&info, &semaphore);

        return semaphore;
    }
}
