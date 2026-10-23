using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Utils;
using Magic.SdlBindings;
using Magic.Services;
using Vortice.Vulkan;
using static Magic.SdlBindings.SDL3;
using SdlVkInstance = Magic.SdlBindings.VkInstance_T;
using SdlVkSurfaceKHR = Magic.SdlBindings.VkSurfaceKHR_T;
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

    /// <summary>
    /// Whether the driver can say why the GPU was lost (VK_EXT_device_fault), and whether it has been asked: only the
    /// first loss is logged, as every wait and submit after it fails the same way.
    /// </summary>
    private readonly bool _canReportFaults;
    private bool _isFaultLogged;

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

        _hasVideo = SDL_InitSubSystem(SDL_INIT_VIDEO);
        _hasSdlVulkan = _hasVideo && SDL_Vulkan_LoadLibrary(null);
        List<VkUtf8String> extensions = GetSurfaceExtensions();
        HasSurfaces = extensions.Count > 0;
        HasDebugUtils = HasInstanceExtension(Vulkan.VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
        HasColorSpaces = HasInstanceExtension(Vulkan.VK_EXT_SWAPCHAIN_COLOR_SPACE_EXTENSION_NAME);
        if (HasDebugUtils) extensions.Add(Vulkan.VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
        if (HasColorSpaces) extensions.Add(Vulkan.VK_EXT_SWAPCHAIN_COLOR_SPACE_EXTENSION_NAME);

        bool isValidated = settings.IsValidating && HasLayer(Vulkan.VK_LAYER_KHRONOS_VALIDATION_EXTENSION_NAME);
        if (settings.IsValidating && !isValidated)
        {
            Debugging.Warn("Vulkan's validation layer is not installed, so the GPU is not validated: the Vulkan SDK "
                + "installs it.");
        }

        Instance = CreateInstance(extensions, isValidated && HasDebugUtils, isValidated);
        InstanceApi = Vulkan.GetApi(Instance);
        if (isValidated && HasDebugUtils) _messenger = CreateMessenger();

        PhysicalDevice = ChoosePhysicalDevice(settings, out VkPhysicalDeviceProperties properties);
        Limits = properties.limits;
        DescriptorLimits = GetDescriptorLimits(PhysicalDevice);
        Name = $"{CString.Read(properties.deviceName)} (vulkan)";
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out _memory);
        QueueFamily = FindQueueFamily(PhysicalDevice, out uint timestampBits);
        TimestampBits = timestampBits;
        InstanceApi.vkGetPhysicalDeviceFeatures(PhysicalDevice, out VkPhysicalDeviceFeatures supported);
        HasAnisotropy = supported.samplerAnisotropy;
        HasDepthBiasClamp = supported.depthBiasClamp;
        _canReportFaults = CanReportFaults();
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
    /// How many descriptors of each kind a set written after it is bound may hold, which the heap is sized by.
    /// </summary>
    internal VkPhysicalDeviceVulkan12Properties DescriptorLimits { get; }

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
        if (_hasSdlVulkan) SDL_Vulkan_UnloadLibrary();
        if (_hasVideo) SDL_QuitSubSystem(SDL_INIT_VIDEO);
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
        if (result == VkResult.ErrorDeviceLost) LogFault();
    }

    /// <summary>
    /// Logs why the GPU was lost, as far as its driver can say: what happened, each address the GPU faulted at with
    /// what it did there, such as reading memory that is not there, and what the vendor adds. Call it when a wait or
    /// submit returns <see cref="VkResult.ErrorDeviceLost"/>; a driver without VK_EXT_device_fault says nothing more.
    /// </summary>
    internal void LogFault()
    {
        if (!_canReportFaults || _isFaultLogged) return;

        _isFaultLogged = true;

        // The driver is asked twice: how many addresses and vendor entries it has, then what they are.
        VkDeviceFaultCountsEXT counts = new();
        Api.vkGetDeviceFaultInfoEXT(&counts, null);
        VkDeviceFaultAddressInfoKHR[] addresses = new VkDeviceFaultAddressInfoKHR[counts.addressInfoCount];
        VkDeviceFaultVendorInfoKHR[] vendors = new VkDeviceFaultVendorInfoKHR[counts.vendorInfoCount];
        VkDeviceFaultInfoEXT info = new();
        fixed (VkDeviceFaultAddressInfoKHR* firstAddress = addresses)
        fixed (VkDeviceFaultVendorInfoKHR* firstVendor = vendors)
        {
            info.pAddressInfos = firstAddress;
            info.pVendorInfos = firstVendor;
            Api.vkGetDeviceFaultInfoEXT(&counts, &info);
        }

        StringBuilder sb = new($"The GPU was lost: {CString.Read(info.description)}");
        foreach (VkDeviceFaultAddressInfoKHR address in addresses)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"\n{address.addressType} at 0x{address.reportedAddress:X16}, "
                    + $"precise to {address.addressPrecision} bytes");
        }

        foreach (VkDeviceFaultVendorInfoKHR vendor in vendors)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"\n{CString.Read(vendor.description)}: code 0x{vendor.vendorFaultCode:X}, "
                    + $"data 0x{vendor.vendorFaultData:X}");
        }

        Debugging.Error(sb.ToString());
    }

    /// <summary>
    /// A window's surface, made by SDL, whose window it is; none when SDL has no Vulkan.
    /// </summary>
    internal VkSurfaceKHR CreateSurface(SDL_Window* window)
    {
        SdlVkSurfaceKHR* surface = null;

        if (!HasSurfaces || !SDL_Vulkan_CreateSurface(window, (SdlVkInstance*)Instance.Handle, null, &surface))
        {
            throw new InvalidOperationException(
                $"SDL could not make the window a Vulkan surface: {CString.Read(SDL_GetError())}");
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

        return HasExtension(extensions, name);
    }

    /// <summary>
    /// Whether <paramref name="extensions"/>, as Vulkan lists those of the instance or of a GPU, name
    /// <paramref name="name"/>.
    /// </summary>
    private static bool HasExtension(ReadOnlySpan<VkExtensionProperties> extensions, ReadOnlySpan<byte> name)
    {
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
    private static VkDebugUtilsMessengerCreateInfoEXT CreateMessengerInfo() => new()
    {
        messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
        messageType = VkDebugUtilsMessageTypeFlagsEXT.General
            | VkDebugUtilsMessageTypeFlagsEXT.Validation
            | VkDebugUtilsMessageTypeFlagsEXT.Performance,
        pfnUserCallback = &OnValidationMessage,
    };

    /// <summary>
    /// Whether the GPU reads resources through the heap as its shaders do: arrays of them indexed by any lane, holding
    /// nothing in most slots, written while frames read other slots; storage images read and written whatever their
    /// format; and storage written from every stage, as the heap's buffers and levels are writable in each.
    /// </summary>
    private static bool HasHeapFeatures(
        in VkPhysicalDeviceVulkan12Features features12,
        in VkPhysicalDeviceFeatures core)
        => features12.runtimeDescriptorArray
            && features12.descriptorBindingPartiallyBound
            && features12.descriptorBindingUpdateUnusedWhilePending
            && features12.descriptorBindingSampledImageUpdateAfterBind
            && features12.descriptorBindingStorageImageUpdateAfterBind
            && features12.descriptorBindingStorageBufferUpdateAfterBind
            && features12.shaderSampledImageArrayNonUniformIndexing
            && features12.shaderStorageImageArrayNonUniformIndexing
            && features12.shaderStorageBufferArrayNonUniformIndexing
            && core.shaderStorageImageReadWithoutFormat
            && core.shaderStorageImageWriteWithoutFormat
            && core.vertexPipelineStoresAndAtomics
            && core.fragmentStoresAndAtomics;

    /// <summary>
    /// The instance extensions windows' surfaces need on this platform, as SDL names them; none where SDL has no Vulkan.
    /// </summary>
    private List<VkUtf8String> GetSurfaceExtensions()
    {
        if (!_hasSdlVulkan) return [];

        uint count;
        byte** names = SDL_Vulkan_GetInstanceExtensions(&count);
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
            VkDebugUtilsMessengerCreateInfoEXT messenger = CreateMessengerInfo();
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
        VkDebugUtilsMessengerCreateInfoEXT info = CreateMessengerInfo();
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

        VkPhysicalDeviceType preferred = settings.IsLowPower
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
                + "synchronization2, timeline semaphores, descriptor indexing, multi-draw indirect and depth clamping: "
                + "update the GPU's driver.");
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
            && HasHeapFeatures(features12, features.features)
            && features.features.multiDrawIndirect
            && features.features.depthClamp
            && features.features.independentBlend
            && features.features.imageCubeArray;
    }

    private VkPhysicalDeviceVulkan12Properties GetDescriptorLimits(VkPhysicalDevice device)
    {
        VkPhysicalDeviceVulkan12Properties properties12 = new();
        VkPhysicalDeviceProperties2 properties = new() { pNext = &properties12 };
        InstanceApi.vkGetPhysicalDeviceProperties2(device, &properties);

        return properties12;
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
    /// Whether the driver can say why the GPU was lost: it has VK_EXT_device_fault, with the feature that reports it.
    /// </summary>
    private bool CanReportFaults()
    {
        InstanceApi.vkEnumerateDeviceExtensionProperties(PhysicalDevice, out uint count);
        Span<VkExtensionProperties> extensions = new VkExtensionProperties[count];
        InstanceApi.vkEnumerateDeviceExtensionProperties(PhysicalDevice, extensions);

        if (!HasExtension(extensions, Vulkan.VK_EXT_DEVICE_FAULT_EXTENSION_NAME)) return false;

        VkPhysicalDeviceFaultFeaturesEXT fault = new();
        VkPhysicalDeviceFeatures2 features = new() { pNext = &fault };
        InstanceApi.vkGetPhysicalDeviceFeatures2(PhysicalDevice, &features);

        return fault.deviceFault;
    }

    /// <summary>
    /// The device with the features the gem needs, which choosing it made sure of, and those it uses where they are
    /// there: anisotropy, a clamp on depth bias, an instance offset in indirect draws, clip distances, the driver's
    /// report of why the GPU was lost.
    /// </summary>
    private VkDevice CreateDevice(in VkPhysicalDeviceFeatures supported)
    {
        VkPhysicalDeviceFaultFeaturesEXT fault = new() { deviceFault = true };
        VkPhysicalDeviceVulkan13Features features13 = new()
        {
            pNext = _canReportFaults ? &fault : null,
            dynamicRendering = true,
            synchronization2 = true,
        };
        VkPhysicalDeviceVulkan12Features features12 = new()
        {
            pNext = &features13,
            timelineSemaphore = true,
            hostQueryReset = true,
            runtimeDescriptorArray = true,
            descriptorBindingPartiallyBound = true,
            descriptorBindingUpdateUnusedWhilePending = true,
            descriptorBindingSampledImageUpdateAfterBind = true,
            descriptorBindingStorageImageUpdateAfterBind = true,
            descriptorBindingStorageBufferUpdateAfterBind = true,
            shaderSampledImageArrayNonUniformIndexing = true,
            shaderStorageImageArrayNonUniformIndexing = true,
            shaderStorageBufferArrayNonUniformIndexing = true,
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
                shaderStorageImageReadWithoutFormat = true,
                shaderStorageImageWriteWithoutFormat = true,
                vertexPipelineStoresAndAtomics = true,
                fragmentStoresAndAtomics = true,
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
        if (_canReportFaults) names.Add(Vulkan.VK_EXT_DEVICE_FAULT_EXTENSION_NAME);

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
