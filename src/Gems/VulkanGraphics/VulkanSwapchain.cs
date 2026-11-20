using Magic.Debugging;
using Magic.Graphics;
using Magic.Platform;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The window's images, each a texture of the heap while it lasts.
/// </summary>
/// <remarks>
/// They are made for the window's size and the vsync setting, and made again when either changes or the surface says
/// they are out of date. A frame takes an image with <see cref="Acquire"/> and gives it back with
/// <see cref="Present"/>.
/// </remarks>
internal sealed unsafe class VulkanSwapchain : Disposable
{
    /// <summary>
    /// How frames reach the window without waiting for the display, most wanted first: immediate, which can tear, else
    /// mailbox, which never tears but which some drivers hold to the display's rate, as NVIDIA's does on a laptop panel
    /// another GPU drives.
    /// </summary>
    private static readonly VkPresentModeKHR[] _unsyncedModes = [VkPresentModeKHR.Immediate, VkPresentModeKHR.Mailbox];

    private readonly VulkanDevice _device;

    private readonly VulkanResources _resources;

    private readonly IWindow _window;

    private VkSwapchainKHR _swapchain = VkSwapchainKHR.Null;

    private GpuTexture[] _textures = [];

    private VkSemaphore[] _rendered = [];

    private int _width;

    private int _height;

    private bool _isVSyncOn;

    private int _acquired = -1;

    public VulkanSwapchain(VulkanDevice device, VulkanResources resources, IWindow window)
    {
        _device = device;
        _resources = resources;
        _window = window;
        Track(Destroy);
    }

    /// <summary>
    /// Signalled when the current image is drawn, which presenting waits for.
    /// </summary>
    public VkSemaphore Rendered => _acquired < 0 ? VkSemaphore.Null : _rendered[_acquired];

    /// <summary>
    /// Takes the next image for a frame, remaking the images first when the window or <paramref name="isVSyncOn"/>
    /// changed; none when the window is too small to draw into, or the surface gave none.
    /// </summary>
    public GpuTexture Acquire(VkSemaphore acquired, bool isVSyncOn)
    {
        _acquired = -1;
        if (_window.Width != _width || _window.Height != _height || isVSyncOn != _isVSyncOn || _swapchain.IsNull)
        {
            _isVSyncOn = isVSyncOn;
            Recreate();
        }

        if (_swapchain.IsNull)
            return GpuTexture.None;

        uint index;
        VkResult result = _device.Api.vkAcquireNextImageKHR(_swapchain, ulong.MaxValue, acquired, VkFence.Null, &index);
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            Recreate();
            if (_swapchain.IsNull)
                return GpuTexture.None;

            result = _device.Api.vkAcquireNextImageKHR(_swapchain, ulong.MaxValue, acquired, VkFence.Null, &index);
        }

        if (result is not (VkResult.Success or VkResult.SuboptimalKHR))
        {
            Log.Warn($"The window gave no image to draw into: {result}.");

            return GpuTexture.None;
        }

        _acquired = (int)index;
        // What the image held is gone with the last present: it is drawn into from nothing.
        if (_resources.Textures[_textures[_acquired].Id] is { } texture)
            texture.Accesses[0] = GpuAccess.None;

        return _textures[_acquired];
    }

    /// <summary>
    /// Shows the acquired image once <see cref="Rendered"/> is signalled.
    /// </summary>
    public void Present()
    {
        if (_acquired < 0)
            return;

        VkResult result = _device.Api.vkQueuePresentKHR(_device.Queue, Rendered, _swapchain, (uint)_acquired);
        _acquired = -1;
        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            _width = -1;
        else if (result != VkResult.Success)
            Log.Warn($"The window did not take its image: {result}.");
    }

    private void Destroy()
    {
        if (_swapchain.IsNull)
            return;

        _device.Api.vkDeviceWaitIdle();
        foreach (GpuTexture texture in _textures)
        {
            if (_resources.Textures.Remove(texture.Id) is { } removed)
                _resources.Free(new(texture.Id, removed));
        }

        foreach (VkSemaphore semaphore in _rendered)
        {
            _device.Api.vkDestroySemaphore(semaphore);
        }

        _device.Api.vkDestroySwapchainKHR(_swapchain);
        _swapchain = VkSwapchainKHR.Null;
        _textures = [];
        _rendered = [];
    }

    private void Recreate()
    {
        Destroy();
        _width = _window.Width;
        _height = _window.Height;
        VkSurfaceKHR surface = _device.Surface;
        _device.InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(
            _device.PhysicalDevice,
            surface,
            out VkSurfaceCapabilitiesKHR capabilities
        );
        uint width =
            capabilities.currentExtent.width == uint.MaxValue
                ? (uint)
                    Math.Clamp(_width, (int)capabilities.minImageExtent.width, (int)capabilities.maxImageExtent.width)
                : capabilities.currentExtent.width;
        uint height =
            capabilities.currentExtent.height == uint.MaxValue
                ? (uint)
                    Math.Clamp(
                        _height,
                        (int)capabilities.minImageExtent.height,
                        (int)capabilities.maxImageExtent.height
                    )
                : capabilities.currentExtent.height;
        if (width == 0 || height == 0)
            return;

        VkSurfaceFormatKHR format = PickFormat(surface);
        _device.InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(
            _device.PhysicalDevice,
            surface,
            out uint modeCount
        );
        VkPresentModeKHR[] modes = new VkPresentModeKHR[modeCount];
        _device.InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(_device.PhysicalDevice, surface, modes);
        // Each image is as big as the window, so there are as few as the surface takes: two at least, one drawn while
        // the other is shown, and one more for mailbox to queue a drawn image while one is shown.
        VkPresentModeKHR presentMode = PickPresentMode(modes);
        uint queued = presentMode == VkPresentModeKHR.Mailbox ? 1u : 0u;
        uint count = Math.Max(capabilities.minImageCount + queued, 2);
        if (capabilities.maxImageCount > 0)
            count = Math.Min(count, capabilities.maxImageCount);

        VkSwapchainCreateInfoKHR createInfo = new()
        {
            surface = surface,
            minImageCount = count,
            imageFormat = format.format,
            imageColorSpace = format.colorSpace,
            imageExtent = new VkExtent2D(width, height),
            imageArrayLayers = 1,
            imageUsage =
                VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = presentMode,
            clipped = true,
        };
        // The driver must not own the display, even when the window fills it: the NVIDIA driver then crashes in
        // vkDestroyDevice at exit.
        VkSurfaceFullScreenExclusiveInfoEXT notExclusive = new()
        {
            fullScreenExclusive = VkFullScreenExclusiveEXT.Disallowed,
        };
        if (_device.HasFullScreenExclusive)
            createInfo.pNext = &notExclusive;

        VkResult result = _device.Api.vkCreateSwapchainKHR(&createInfo, out VkSwapchainKHR swapchain);
        if (result != VkResult.Success)
        {
            Log.Error($"The window's images could not be made: {result}.");

            return;
        }

        _swapchain = swapchain;
        _device.Api.vkGetSwapchainImagesKHR(_swapchain, out uint imageCount);
        Log.Info($"Window images: {imageCount} of {width}x{height}, {presentMode} of {string.Join(", ", modes)}.");
        VkImage[] images = new VkImage[imageCount];
        _device.Api.vkGetSwapchainImagesKHR(_swapchain, images);
        _textures = new GpuTexture[imageCount];
        _rendered = new VkSemaphore[imageCount];
        TextureDescription description = new(
            "Window",
            (int)width,
            (int)height,
            format.format.ToGpu(),
            TextureUsages.ColorTarget | TextureUsages.CopyDestination | TextureUsages.CopySource
        );
        for (int i = 0; i < imageCount; i++)
        {
            Result<GpuTexture> texture = _resources.AddWindowTexture(images[i], description);

            if (!texture.Ok)
            {
                Log.Error($"The window's image {i} has no place in the heap: {texture.Error}");
                Destroy();

                return;
            }

            _textures[i] = texture.Value;
            _device.Api.vkCreateSemaphore(out _rendered[i]);
        }
    }

    /// <summary>
    /// Picks an sRGB 8-bit format that the surface has, else its first format.
    /// </summary>
    private VkSurfaceFormatKHR PickFormat(VkSurfaceKHR surface)
    {
        _device.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_device.PhysicalDevice, surface, out uint count);
        Span<VkSurfaceFormatKHR> formats = new VkSurfaceFormatKHR[count];
        _device.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_device.PhysicalDevice, surface, formats);
        foreach (VkSurfaceFormatKHR format in formats)
        {
            bool isSrgb = format.format is VkFormat.B8G8R8A8Srgb or VkFormat.R8G8B8A8Srgb;
            if (isSrgb && format.colorSpace == VkColorSpaceKHR.SrgbNonLinear)
                return format;
        }

        foreach (VkSurfaceFormatKHR format in formats)
        {
            if (format.format.ToGpu() != GpuFormat.Unknown)
                return format;
        }

        return formats.Length > 0
            ? formats[0]
            : new VkSurfaceFormatKHR(VkFormat.B8G8R8A8Unorm, VkColorSpaceKHR.SrgbNonLinear);
    }

    /// <summary>
    /// Picks FIFO while vsync is on, because it waits for the display and every surface has it.
    /// </summary>
    /// <remarks>
    /// Otherwise it picks the first of <see cref="_unsyncedModes"/> that the surface has in <paramref name="modes"/>,
    /// else FIFO.
    /// </remarks>
    private VkPresentModeKHR PickPresentMode(VkPresentModeKHR[] modes)
    {
        if (_isVSyncOn)
            return VkPresentModeKHR.Fifo;

        foreach (VkPresentModeKHR mode in _unsyncedModes)
        {
            if (modes.Contains(mode))
                return mode;
        }

        return VkPresentModeKHR.Fifo;
    }
}
