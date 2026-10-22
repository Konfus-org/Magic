using System.Runtime.InteropServices;
using Magic.Contexts.Utils;
using Magic.SdlBindings;
using Magic.Services;
using Vortice.Vulkan;
using SdlVkInstance = Magic.SdlBindings.VkInstance;
using SdlVkSurfaceKHR = Magic.SdlBindings.VkSurfaceKHR;
using VkInstance = Vortice.Vulkan.VkInstance;
using VkPhysicalDevice = Vortice.Vulkan.VkPhysicalDevice;
using VkSurfaceKHR = Vortice.Vulkan.VkSurfaceKHR;

namespace Magic.VulkanGpu;

/// <summary>
/// Vulkan, opened: the instance, validated when the settings ask and the layer is installed; the GPU the settings pick
/// among those that run Vulkan 1.3; its device, with the one queue every frame is submitted to, which draws, computes,
/// copies and presents; and the timeline semaphore that counts the frames the GPU has finished. A window's surface is
/// made through SDL, the platform gem's, which knows the window: this starts SDL's video subsystem for it, which a
/// headless platform leaves down, and stops it again.
/// </summary>
internal sealed unsafe class VulkanDevice
{
    /// <summary>
    /// The validation layer's messages logged so far, by name: each kind is logged once, then only verbosely, as one
    /// mistake made every frame would otherwise bury the rest.
    /// </summary>
    private static readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private static readonly Lock _loggedLock = new();

    private readonly VkDebugUtilsMessengerEXT _messenger;
    private readonly VkPhysicalDeviceMemoryProperties _memory;

    /// <summary>
    /// SDL's video subsystem and its Vulkan library, each started here and so stopped in <see cref="Destroy"/>; without
    /// them, as headless where no display is, no window has a surface.
    /// </summary>
    private readonly bool _hasVideo;
    private readonly bool _hasSdlVulkan;

    public VulkanDevice(VulkanGpuSettings settings)
    {
        if (Vulkan.vkInitialize() != VkResult.Success)
        {
            throw new InvalidOperationException("Vulkan is not installed: no Vulkan loader was found. The GPU's driver "
                + "installs it.");
        }

        if (Vulkan.vkEnumerateInstanceVersion() < VkVersion.Version_1_3)
        {
            throw new InvalidOperationException(
                $"The Vulkan installed is {Vulkan.vkEnumerateInstanceVersion()}; Magic needs 1.3: update the GPU's driver.");
        }

        _hasVideo = SDL.InitSubSystem(SDL.InitVideo);
        _hasSdlVulkan = _hasVideo && SDL.VulkanLoadLibrary(null);
        List<VkUtf8String> extensions = GetSurfaceExtensions();
        HasSurfaces = extensions.Count > 0;
        HasDebugUtils = HasInstanceExtension(Vulkan.VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
        HasColorSpaces = HasInstanceExtension(Vulkan.VK_EXT_SWAPCHAIN_COLOR_SPACE_EXTENSION_NAME);
        if (HasDebugUtils) extensions.Add(Vulkan.VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
        if (HasColorSpaces) extensions.Add(Vulkan.VK_EXT_SWAPCHAIN_COLOR_SPACE_EXTENSION_NAME);

        bool isValidated = settings.Validation && HasLayer(Vulkan.VK_LAYER_KHRONOS_VALIDATION_EXTENSION_NAME);
        if (settings.Validation && !isValidated)
        {
            Debugging.Warn("Vulkan's validation layer is not installed, so the GPU is not validated: the Vulkan SDK "
                + "installs it.");
        }

        Instance = CreateInstance(extensions, isValidated && HasDebugUtils, isValidated);
        InstanceApi = Vulkan.GetApi(Instance);
        if (isValidated && HasDebugUtils) _messenger = CreateMessenger();

        PhysicalDevice = ChoosePhysicalDevice(settings, out VkPhysicalDeviceProperties properties);
        Limits = properties.limits;
        Name = $"{CString.Read(properties.deviceName)} (vulkan)";
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out _memory);
        QueueFamily = FindQueueFamily(PhysicalDevice, out uint timestampBits);
        TimestampBits = timestampBits;
        InstanceApi.vkGetPhysicalDeviceFeatures(PhysicalDevice, out VkPhysicalDeviceFeatures supported);
        HasAnisotropy = supported.samplerAnisotropy;
        HasDepthBiasClamp = supported.depthBiasClamp;
        Handle = CreateDevice(supported);
        Api = Vulkan.GetApi(Instance, Handle);
        Api.vkGetDeviceQueue(QueueFamily, 0, out VkQueue queue);
        Queue = queue;
        Timeline = CreateTimeline();
        VkVersion version = properties.apiVersion;
        Debugging.Info(
            $"GPU: {Name}, Vulkan {version.Major}.{version.Minor}.{version.Patch}, validation {isValidated}");
    }

    internal VkInstance Instance { get; }

    internal VkInstanceApi InstanceApi { get; }

    internal VkPhysicalDevice PhysicalDevice { get; }

    internal VkDevice Handle { get; }

    internal VkDeviceApi Api { get; }

    internal VkQueue Queue { get; }

    internal uint QueueFamily { get; }

    /// <summary>
    /// The bits of a timestamp the queue writes that mean something; none where it writes none.
    /// </summary>
    internal uint TimestampBits { get; }

    internal VkPhysicalDeviceLimits Limits { get; }

    /// <summary>
    /// The GPU and the API, for logs: <c>NVIDIA GeForce RTX 4070 (vulkan)</c>.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Whether windows can have surfaces: SDL named the extensions they need, which only it knows for the platform.
    /// </summary>
    internal bool HasSurfaces { get; }

    /// <summary>
    /// Whether command buffers can carry debug labels, which a GPU debugger shows the frame's passes by.
    /// </summary>
    internal bool HasDebugUtils { get; }

    /// <summary>
    /// Whether a window can present in a colour space other than sRGB: the HDR compositions need it.
    /// </summary>
    internal bool HasColorSpaces { get; }

    /// <summary>
    /// Whether a sampler can filter anisotropically; one that asks for it filters plainly where it cannot.
    /// </summary>
    internal bool HasAnisotropy { get; }

    /// <summary>
    /// Whether a pipeline's depth bias can be clamped; one that asks for a clamp is biased unclamped where it cannot.
    /// </summary>
    internal bool HasDepthBiasClamp { get; }

    /// <summary>
    /// Counts the frames the GPU has finished: a frame's submit signals its number, and whoever must wait for it waits
    /// for that value.
    /// </summary>
    internal VkSemaphore Timeline { get; }

    /// <summary>
    /// Closes Vulkan, once everything made through it is gone, and SDL's video subsystem with it where this started it.
    /// </summary>
    internal void Destroy()
    {
        Api.vkDeviceWaitIdle();
        Api.vkDestroySemaphore(Timeline);
        Api.vkDestroyDevice();
        if (_messenger.IsNotNull) InstanceApi.vkDestroyDebugUtilsMessengerEXT(_messenger);

        InstanceApi.vkDestroyInstance();
        if (_hasSdlVulkan) SDL.VulkanUnloadLibrary();
        if (_hasVideo) SDL.QuitSubSystem(SDL.InitVideo);
    }

    /// <summary>
    /// The first memory type among <paramref name="typeBits"/> that has every one of <paramref name="required"/>.
    /// </summary>
    internal uint FindMemoryType(uint typeBits, VkMemoryPropertyFlags required)
    {
        for (int i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (_memory.memoryTypes[i].propertyFlags & required) == required)
                return (uint)i;
        }

        throw new InvalidOperationException($"The GPU has no memory that is {required}.");
    }

    /// <summary>
    /// The number of the last frame the GPU has finished.
    /// </summary>
    internal ulong GetFinished()
    {
        ulong value;
        Api.vkGetSemaphoreCounterValue(Timeline, &value);

        return value;
    }

    /// <summary>
    /// Waits until the GPU has finished frame <paramref name="frame"/>: for a frame slot to be reused, or a read back.
    /// </summary>
    internal void WaitFor(ulong frame)
    {
        if (frame == 0) return;

        VkSemaphore timeline = Timeline;
        VkSemaphoreWaitInfo wait = new() { semaphoreCount = 1, pSemaphores = &timeline, pValues = &frame };
        VkResult result = Api.vkWaitSemaphores(&wait, ulong.MaxValue);
        if (result != VkResult.Success) Debugging.Error($"Waiting for frame {frame} on the GPU failed: {result}");
    }

    /// <summary>
    /// A window's surface, made by SDL, whose window it is; none when SDL has no Vulkan.
    /// </summary>
    internal VkSurfaceKHR CreateSurface(SDL.Window* window)
    {
        SdlVkSurfaceKHR* surface = null;

        if (!HasSurfaces || !SDL.VulkanCreateSurface(window, (SdlVkInstance*)Instance.Handle, null, &surface))
        {
            throw new InvalidOperationException(
                $"SDL could not make the window a Vulkan surface: {CString.Read(SDL.GetError())}");
        }

        return new VkSurfaceKHR((ulong)surface);
    }

    /// <summary>
    /// Logs what the validation layer says: an error as an error, so a run that fails on errors fails on it, a warning
    /// as a warning, each kind once. It runs on whichever thread made the call it is about, and must not throw back into
    /// the driver.
    /// </summary>
    [UnmanagedCallersOnly]
    private static uint OnValidationMessage(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* user)
    {
        string message = CString.Read(data->pMessage);
        string kind = data->pMessageIdName == null ? message : CString.Read(data->pMessageIdName);
        bool isFirst;
        lock (_loggedLock)
        {
            isFirst = _logged.Add(kind);
        }

        bool isError = (severity & VkDebugUtilsMessageSeverityFlagsEXT.Error)
            != VkDebugUtilsMessageSeverityFlagsEXT.None;
        bool isGeneral = (types & ~VkDebugUtilsMessageTypeFlagsEXT.General) == VkDebugUtilsMessageTypeFlagsEXT.None;

        // What the loader says of itself, where it found the layers, is no finding about this gem's use of Vulkan.
        if (!isFirst || isGeneral) Debugging.Verbose($"Vulkan: {message}");
        else if (isError) Debugging.Error($"Vulkan: {message}");
        else Debugging.Warn($"Vulkan: {message}");

        return Vulkan.VK_FALSE;
    }

    private static bool HasInstanceExtension(ReadOnlySpan<byte> name)
    {
        Vulkan.vkEnumerateInstanceExtensionProperties(out uint count);
        Span<VkExtensionProperties> extensions = new VkExtensionProperties[count];
        Vulkan.vkEnumerateInstanceExtensionProperties(extensions);
        foreach (VkExtensionProperties extension in extensions)
        {
            if (name.SequenceEqual(
                MemoryMarshal.CreateReadOnlySpanFromNullTerminated(extension.extensionName)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLayer(ReadOnlySpan<byte> name)
    {
        uint count;
        Vulkan.vkEnumerateInstanceLayerProperties(&count, null);
        VkLayerProperties[] layers = new VkLayerProperties[count];
        fixed (VkLayerProperties* first = layers)
        {
            Vulkan.vkEnumerateInstanceLayerProperties(&count, first);
        }

        foreach (VkLayerProperties layer in layers)
        {
            if (name.SequenceEqual(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(layer.layerName))) return true;
        }

        return false;
    }

    /// <summary>
    /// The validation layer's messages that are worth reading: warnings and errors, of every kind.
    /// </summary>
    private static VkDebugUtilsMessengerCreateInfoEXT MakeMessengerInfo() => new()
    {
        messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
        messageType = VkDebugUtilsMessageTypeFlagsEXT.General
            | VkDebugUtilsMessageTypeFlagsEXT.Validation
            | VkDebugUtilsMessageTypeFlagsEXT.Performance,
        pfnUserCallback = &OnValidationMessage,
    };

    /// <summary>
    /// The instance extensions windows' surfaces need on this platform, as SDL names them; none where SDL has no Vulkan.
    /// </summary>
    private List<VkUtf8String> GetSurfaceExtensions()
    {
        if (!_hasSdlVulkan) return [];

        uint count;
        byte** names = SDL.VulkanGetInstanceExtensions(&count);
        List<VkUtf8String> extensions = [];
        for (int i = 0; names != null && i < count; i++)
        {
            extensions.Add(new VkUtf8String(names[i]));
        }

        return extensions;
    }

    private VkInstance CreateInstance(List<VkUtf8String> extensions, bool isMessaged, bool isValidated)
    {
        List<VkUtf8String> layers = [];
        if (isValidated) layers.Add(Vulkan.VK_LAYER_KHRONOS_VALIDATION_EXTENSION_NAME);

        using VkStringArray extensionNames = new(extensions);
        using VkStringArray layerNames = new(layers);
        fixed (byte* engine = "Magic"u8)
        {
            VkApplicationInfo application = new()
            {
                pApplicationName = engine,
                pEngineName = engine,
                apiVersion = VkVersion.Version_1_3,
            };
            VkDebugUtilsMessengerCreateInfoEXT messenger = MakeMessengerInfo();
            VkInstanceCreateInfo info = new()
            {
                pNext = isMessaged ? &messenger : null,
                pApplicationInfo = &application,
                enabledLayerCount = layerNames.Length,
                ppEnabledLayerNames = layerNames,
                enabledExtensionCount = extensionNames.Length,
                ppEnabledExtensionNames = extensionNames,
            };
            VkInstance instance;
            VkResult result = Vulkan.vkCreateInstance(&info, &instance);

            return result == VkResult.Success
                ? instance
                : throw new InvalidOperationException($"Vulkan would not open: {result}.");
        }
    }

    private VkDebugUtilsMessengerEXT CreateMessenger()
    {
        VkDebugUtilsMessengerCreateInfoEXT info = MakeMessengerInfo();
        VkDebugUtilsMessengerEXT messenger;
        InstanceApi.vkCreateDebugUtilsMessengerEXT(&info, &messenger);

        return messenger;
    }

    /// <summary>
    /// The GPU to open: the first whose name contains what the settings name; else the fastest, discrete over
    /// integrated, or the integrated one when the settings ask to save power. Only one that runs Vulkan 1.3 with every
    /// feature the gem needs is a candidate.
    /// </summary>
    private VkPhysicalDevice ChoosePhysicalDevice(VulkanGpuSettings settings, out VkPhysicalDeviceProperties chosen)
    {
        uint count;
        InstanceApi.vkEnumeratePhysicalDevices(&count, null);
        VkPhysicalDevice[] devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* first = devices)
        {
            InstanceApi.vkEnumeratePhysicalDevices(&count, first);
        }

        VkPhysicalDeviceType preferred = settings.LowPower
            ? VkPhysicalDeviceType.IntegratedGpu
            : VkPhysicalDeviceType.DiscreteGpu;
        VkPhysicalDevice best = default;
        int bestScore = -1;
        chosen = default;
        foreach (VkPhysicalDevice device in devices)
        {
            InstanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties properties);

            if (properties.apiVersion < VkVersion.Version_1_3 || !HasRequiredFeatures(device)) continue;

            string name = CString.Read(properties.deviceName);
            bool isNamed = settings.Device.Length > 0
                && name.Contains(settings.Device, StringComparison.OrdinalIgnoreCase);
            int score = (isNamed, properties.deviceType) switch
            {
                (true, _) => 3,
                (false, VkPhysicalDeviceType type) when type == preferred => 2,
                (false, VkPhysicalDeviceType.Cpu) => 0,
                _ => 1,
            };

            if (score <= bestScore) continue;

            best = device;
            bestScore = score;
            chosen = properties;
        }

        return best.IsNotNull
            ? best
            : throw new InvalidOperationException("No GPU here runs Vulkan 1.3 with dynamic rendering, "
                + "synchronization2, timeline semaphores, multi-draw indirect and depth clamping: update the GPU's "
                + "driver.");
    }

    private bool HasRequiredFeatures(VkPhysicalDevice device)
    {
        VkPhysicalDeviceVulkan13Features features13 = new();
        VkPhysicalDeviceVulkan12Features features12 = new() { pNext = &features13 };
        VkPhysicalDeviceFeatures2 features = new() { pNext = &features12 };
        InstanceApi.vkGetPhysicalDeviceFeatures2(device, &features);

        return features13.dynamicRendering
            && features13.synchronization2
            && features12.timelineSemaphore
            && features12.hostQueryReset
            && features.features.multiDrawIndirect
            && features.features.depthClamp
            && features.features.independentBlend
            && features.features.imageCubeArray;
    }

    /// <summary>
    /// The queue family that draws and computes: the one queue every frame goes to.
    /// </summary>
    private uint FindQueueFamily(VkPhysicalDevice device, out uint timestampBits)
    {
        uint count;
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        VkQueueFamilyProperties[] families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* first = families)
        {
            InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, first);
        }

        for (uint i = 0; i < count; i++)
        {
            VkQueueFlags flags = families[i].queueFlags;

            if ((flags & VkQueueFlags.Graphics) == VkQueueFlags.None
                || (flags & VkQueueFlags.Compute) == VkQueueFlags.None)
            {
                continue;
            }

            timestampBits = families[i].timestampValidBits;

            return i;
        }

        throw new InvalidOperationException("The GPU has no queue that both draws and computes.");
    }

    /// <summary>
    /// The device with the features the gem needs, which choosing it made sure of, and those it uses where they are
    /// there: anisotropy, a clamp on depth bias, an instance offset in indirect draws, clip distances.
    /// </summary>
    private VkDevice CreateDevice(in VkPhysicalDeviceFeatures supported)
    {
        VkPhysicalDeviceVulkan13Features features13 = new() { dynamicRendering = true, synchronization2 = true };
        VkPhysicalDeviceVulkan12Features features12 = new()
        {
            pNext = &features13,
            timelineSemaphore = true,
            hostQueryReset = true,
        };
        VkPhysicalDeviceFeatures2 features = new()
        {
            pNext = &features12,
            features = new VkPhysicalDeviceFeatures
            {
                multiDrawIndirect = true,
                depthClamp = true,
                independentBlend = true,
                imageCubeArray = true,
                samplerAnisotropy = supported.samplerAnisotropy,
                depthBiasClamp = supported.depthBiasClamp,
                drawIndirectFirstInstance = supported.drawIndirectFirstInstance,
                shaderClipDistance = supported.shaderClipDistance,
            },
        };

        float priority = 1;
        VkDeviceQueueCreateInfo queue = new()
        {
            queueFamilyIndex = QueueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        List<VkUtf8String> names = [];
        if (HasSurfaces) names.Add(Vulkan.VK_KHR_SWAPCHAIN_EXTENSION_NAME);

        using VkStringArray extensions = new(names);
        VkDeviceCreateInfo info = new()
        {
            pNext = &features,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queue,
            enabledExtensionCount = extensions.Length,
            ppEnabledExtensionNames = extensions,
        };
        VkDevice device;
        VkResult result = InstanceApi.vkCreateDevice(PhysicalDevice, &info, &device);

        return result == VkResult.Success
            ? device
            : throw new InvalidOperationException($"The GPU would not open: {result}.");
    }

    private VkSemaphore CreateTimeline()
    {
        VkSemaphoreTypeCreateInfo type = new() { semaphoreType = VkSemaphoreType.Timeline };
        VkSemaphoreCreateInfo info = new() { pNext = &type };
        VkSemaphore timeline;
        Api.vkCreateSemaphore(&info, &timeline);

        return timeline;
    }
}
