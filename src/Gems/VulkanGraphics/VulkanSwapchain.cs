using Magic.Debugging;
using Magic.Gpu;
using Magic.Platform;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The window's images: made for the window's size, each a texture of the heap while it lasts, remade when the window
/// changes size or the surface says they are out of date. A frame takes one with <see cref="Acquire"/> and gives it
/// back with <see cref="Present"/>.
/// </summary>
internal sealed unsafe class VulkanSwapchain(VulkanDevice device, VulkanResources resources, IWindow window)
    : IDisposable
{
    /// <summary>
    /// How frames reach the window, most wanted first. A Debug build shows them as fast as they are drawn, so its frame
    /// times are what the frame costs: mailbox, which never tears, else immediate. A release waits for the display.
    /// </summary>
#if DEBUG
    private static readonly VkPresentModeKHR[] _presentModes = [VkPresentModeKHR.Mailbox, VkPresentModeKHR.Immediate];
#else
    private static readonly VkPresentModeKHR[] _presentModes = [VkPresentModeKHR.Fifo];
#endif

    private VkSwapchainKHR _swapchain = VkSwapchainKHR.Null;
    private GpuTexture[] _textures = [];
    private VkSemaphore[] _rendered = [];
    private VkImage[] _images = [];
    private int _width;
    private int _height;
    private int _acquired = -1;

    public GpuFormat Format { get; private set; }

    /// <summary>
    /// The image acquired for this frame; none while the window has no size, or until it was acquired.
    /// </summary>
    public GpuTexture Current => _acquired < 0 ? GpuTexture.None : _textures[_acquired];

    /// <summary>
    /// Signalled when the current image is drawn, which presenting waits for.
    /// </summary>
    public VkSemaphore Rendered => _acquired < 0 ? VkSemaphore.Null : _rendered[_acquired];

    public void Dispose() => Destroy();

    /// <summary>
    /// Takes the next image for a frame, remaking the images first when the window changed; none when the window is
    /// too small to draw into, or the surface gave none.
    /// </summary>
    public GpuTexture Acquire(VkSemaphore acquired)
    {
        _acquired = -1;
        if (window.Width != _width || window.Height != _height || _swapchain.IsNull)
            Recreate();

        if (_swapchain.IsNull)
            return GpuTexture.None;

        uint index;
        VkResult result = device.Api.vkAcquireNextImageKHR(_swapchain, ulong.MaxValue, acquired, VkFence.Null, &index);
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            Recreate();
            if (_swapchain.IsNull)
                return GpuTexture.None;

            result = device.Api.vkAcquireNextImageKHR(_swapchain, ulong.MaxValue, acquired, VkFence.Null, &index);
        }

        if (result is not (VkResult.Success or VkResult.SuboptimalKHR))
        {
            Log.Warn($"The window gave no image to draw into: {result}.");

            return GpuTexture.None;
        }

        _acquired = (int)index;
        // What the image held is gone with the last present: it is drawn into from nothing.
        if (resources.GetTexture(_textures[_acquired].Id) is { } texture)
            texture.Layouts[0] = VkImageLayout.Undefined;

        return _textures[_acquired];
    }

    /// <summary>
    /// Shows the acquired image once <see cref="Rendered"/> is signalled.
    /// </summary>
    public void Present()
    {
        if (_acquired < 0)
            return;

        VkResult result = device.Api.vkQueuePresentKHR(device.Queue, Rendered, _swapchain, (uint)_acquired);
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

        device.Api.vkDeviceWaitIdle();
        foreach (GpuTexture texture in _textures)
        {
            if (resources.RemoveTexture(texture.Id) is { } removed)
                resources.Free(texture.Id, removed);
        }

        foreach (VkSemaphore semaphore in _rendered)
        {
            device.Api.vkDestroySemaphore(semaphore);
        }

        device.Api.vkDestroySwapchainKHR(_swapchain);
        _swapchain = VkSwapchainKHR.Null;
        _textures = [];
        _rendered = [];
        _images = [];
    }

    private void Recreate()
    {
        Destroy();
        _width = window.Width;
        _height = window.Height;
        VkSurfaceKHR surface = device.Surface;
        device.InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(
            device.PhysicalDevice,
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
        uint count = Math.Max(capabilities.minImageCount + 1, 3);
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
            presentMode = PickPresentMode(surface),
            clipped = true,
        };
        VkResult result = device.Api.vkCreateSwapchainKHR(&createInfo, out VkSwapchainKHR swapchain);
        if (result != VkResult.Success)
        {
            Log.Error($"The window's images could not be made: {result}.");

            return;
        }

        _swapchain = swapchain;
        Format = format.format.ToGpu();
        device.Api.vkGetSwapchainImagesKHR(_swapchain, out uint imageCount);
        _images = new VkImage[imageCount];
        device.Api.vkGetSwapchainImagesKHR(_swapchain, _images);
        _textures = new GpuTexture[imageCount];
        _rendered = new VkSemaphore[imageCount];
        TextureDescription description = new()
        {
            Width = (int)width,
            Height = (int)height,
            Format = Format,
            Usage = TextureUsages.ColorTarget | TextureUsages.CopyDestination | TextureUsages.CopySource,
            Name = "Window",
        };
        for (int i = 0; i < imageCount; i++)
        {
            Result<GpuTexture> texture = resources.AddWindowTexture(_images[i], description);
            if (!texture.Ok)
            {
                Log.Error($"The window's image {i} has no place in the heap: {texture.Error}");
                Destroy();

                return;
            }

            _textures[i] = texture.Value;
            device.Api.vkCreateSemaphore(out _rendered[i]);
        }
    }

    /// <summary>
    /// An sRGB 8-bit format the surface has, else its first.
    /// </summary>
    private VkSurfaceFormatKHR PickFormat(VkSurfaceKHR surface)
    {
        device.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, out uint count);
        Span<VkSurfaceFormatKHR> formats = new VkSurfaceFormatKHR[count];
        device.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, formats);
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
    /// The first of <see cref="_presentModes"/> the surface has, else FIFO, which every surface has.
    /// </summary>
    private VkPresentModeKHR PickPresentMode(VkSurfaceKHR surface)
    {
        device.InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, out uint count);
        Span<VkPresentModeKHR> modes = new VkPresentModeKHR[count];
        device.InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, modes);
        foreach (VkPresentModeKHR mode in _presentModes)
        {
            if (modes.Contains(mode))
                return mode;
        }

        return VkPresentModeKHR.Fifo;
    }
}
