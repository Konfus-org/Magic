using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Platform;
using Magic.Utils;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The instance, the GPU picked and its device and queue, made once as the gem starts and freed last. Picks the
/// fastest GPU that has every feature of the baseline (IGraphicsApi) and can show the window, and
/// notes which optional ones it has. Throws when there is no such GPU: the gem can't exist without one.
/// </summary>
internal sealed unsafe class VulkanDevice : IDisposable
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private const string DebugUtilsExtension = "VK_EXT_debug_utils";

    private const string PortabilityEnumerationExtension = "VK_KHR_portability_enumeration";

    private const string PortabilitySubsetExtension = "VK_KHR_portability_subset";

    private const string SwapchainExtension = "VK_KHR_swapchain";

    private const string RayQueryExtension = "VK_KHR_ray_query";

    private const string AccelerationStructureExtension = "VK_KHR_acceleration_structure";

    private const string DeferredHostOperationsExtension = "VK_KHR_deferred_host_operations";

    private const string MemoryBudgetExtension = "VK_EXT_memory_budget";

    /// <summary>
    /// The Vulkan loader's variables for which drivers it loads: the ones it leaves out, by their manifests' names, and
    /// the only ones it loads.
    /// </summary>
    private const string DisabledDriversVariable = "VK_LOADER_DRIVERS_DISABLE";

    private const string SelectedDriversVariable = "VK_LOADER_DRIVERS_SELECT";

    /// <summary>
    /// Dozen's manifests, such as <c>dzn_icd.x64.json</c>.
    /// </summary>
    private const string DozenDrivers = "*dzn*";

    private readonly VkDebugUtilsMessengerEXT _messenger;

    public VulkanDevice(IWindow? window, string gpu, bool wantsValidation)
    {
        VkResult started = vkInitialize();
        if (started != VkResult.Success)
            throw new InvalidOperationException($"Vulkan is not installed on this machine: {started}.");

        // Dozen, Vulkan over Direct3D 12, loads Direct3D 12 and each GPU's Direct3D driver, a few hundred MB, for GPUs a
        // native driver gives already: the loader leaves it out, unless the user picks the drivers or no other driver
        // gives a GPU. It reads the variable as it looks for drivers, so a device made again, as after a restart,
        // decides again.
        bool isSkippingDozen =
            Environment.GetEnvironmentVariable(DisabledDriversVariable) is null
            && Environment.GetEnvironmentVariable(SelectedDriversVariable) is null;
        if (isSkippingDozen)
        {
            Environment.SetEnvironmentVariable(DisabledDriversVariable, DozenDrivers);
            if (!HasGpu())
            {
                Log.Info("Only Dozen gives a GPU, so Vulkan is drawn through Direct3D 12.");
                Environment.SetEnvironmentVariable(DisabledDriversVariable, null);
            }
        }

        WindowHandle handle = window?.Handle ?? default;
        try
        {
            HasValidation = wantsValidation && HasLayer(ValidationLayer);
            HashSet<string> instanceExtensions = GetInstanceExtensions();
            HasDebugUtils = instanceExtensions.Contains(DebugUtilsExtension);
            bool hasPortability = instanceExtensions.Contains(PortabilityEnumerationExtension);
            List<string> wantedExtensions = [.. VulkanSurface.GetExtensions(handle.System)];
            if (HasDebugUtils)
                wantedExtensions.Add(DebugUtilsExtension);

            if (hasPortability)
                wantedExtensions.Add(PortabilityEnumerationExtension);

            Instance = CreateInstance(wantedExtensions, hasPortability);
        }
        finally
        {
            if (isSkippingDozen)
                Environment.SetEnvironmentVariable(DisabledDriversVariable, null);
        }

        InstanceApi = new VkInstanceApi(Instance);
        if (HasDebugUtils && HasValidation)
            _messenger = CreateMessenger();

        if (handle.System != WindowSystem.None)
        {
            Result<VkSurfaceKHR> surface = VulkanSurface.Create(InstanceApi, handle);
            if (!surface.Ok)
                Log.Warn($"The window gives no Vulkan surface, so nothing is drawn into it: {surface.Error}");
            else
                Surface = surface.Value;
        }

        Pick picked = PickPhysicalDevice(gpu);
        PhysicalDevice = picked.Device;
        QueueFamily = picked.QueueFamily;
        Features = picked.Features;
        Limits = picked.Limits;
        TimestampPeriod = picked.TimestampPeriod;
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out VkPhysicalDeviceMemoryProperties memory);
        Memory = memory;
        HasMemoryBudget = picked.Extensions.Contains(MemoryBudgetExtension);
        Device = CreateDevice(picked);
        Api = new VkDeviceApi(InstanceApi, Device);
        Api.vkGetDeviceQueue(QueueFamily, 0, out VkQueue queue);
        Queue = queue;
        string api = $"Vulkan {picked.ApiVersion.Major}.{picked.ApiVersion.Minor}.{picked.ApiVersion.Patch}";
        Description =
        [
            new("magic.gems.vulkan_graphics.api", api),
            new("magic.gems.vulkan_graphics.device", Limits.DeviceName),
            new("magic.gems.vulkan_graphics.device.type", GetKindName(picked.Kind)),
            new("magic.gems.vulkan_graphics.driver", picked.Driver),
        ];
        Log.Info($"GPU: {Limits.DeviceName}, {api}{(HasValidation ? ", validating" : string.Empty)}.");
    }

    public VkInstance Instance { get; }

    public VkInstanceApi InstanceApi { get; }

    public VkPhysicalDevice PhysicalDevice { get; }

    public VkDevice Device { get; }

    public VkDeviceApi Api { get; }

    public VkQueue Queue { get; }

    public uint QueueFamily { get; }

    /// <summary>
    /// The window's surface; null headless, or when the window gave none.
    /// </summary>
    public VkSurfaceKHR Surface { get; } = VkSurfaceKHR.Null;

    public GpuFeatures Features { get; }

    public GpuLimits Limits { get; }

    public VkPhysicalDeviceMemoryProperties Memory { get; }

    /// <summary>
    /// Nanoseconds per timestamp tick.
    /// </summary>
    public float TimestampPeriod { get; }

    public bool HasValidation { get; }

    /// <summary>
    /// The API and the GPU, as the tags of the gem's meter: the API's version, the device's name and kind, and its
    /// driver.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Description { get; }

    public bool HasDebugUtils { get; }

    /// <summary>
    /// Whether the driver says how much memory the process holds in each heap, through
    /// <see cref="GetDeviceMemoryUsage"/>.
    /// </summary>
    public bool HasMemoryBudget { get; }

    public void Dispose()
    {
        Api.vkDeviceWaitIdle();
        Api.vkDestroyDevice();
        if (Surface.IsNotNull)
            InstanceApi.vkDestroySurfaceKHR(Surface);

        if (_messenger.IsNotNull)
            InstanceApi.vkDestroyDebugUtilsMessengerEXT(_messenger);

        InstanceApi.vkDestroyInstance();
        vkShutdown();
    }

    /// <summary>
    /// A memory type with <paramref name="wanted"/> among those <paramref name="typeBits"/> allows; -1 when none.
    /// </summary>
    public int FindMemoryType(uint typeBits, VkMemoryPropertyFlags wanted)
    {
        VkPhysicalDeviceMemoryProperties memory = Memory;
        for (int i = 0; i < memory.memoryTypeCount; i++)
        {
            bool isAllowed = (typeBits & (1u << i)) != 0;
            if (isAllowed && memory.memoryTypes[i].propertyFlags.HasFlag(wanted))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Bytes of device-local memory the driver says the process holds now: the resources, the window's images and the
    /// driver's own. Only with <see cref="HasMemoryBudget"/>.
    /// </summary>
    public long GetDeviceMemoryUsage()
    {
        VkPhysicalDeviceMemoryBudgetPropertiesEXT budget = new();
        VkPhysicalDeviceMemoryProperties2 properties = new() { pNext = &budget };
        InstanceApi.vkGetPhysicalDeviceMemoryProperties2(PhysicalDevice, &properties);
        long bytes = 0;
        for (int i = 0; i < properties.memoryProperties.memoryHeapCount; i++)
        {
            if (properties.memoryProperties.memoryHeaps[i].flags.HasFlag(VkMemoryHeapFlags.DeviceLocal))
                bytes += (long)budget.heapUsage[i];
        }

        return bytes;
    }

    [UnmanagedCallersOnly]
    private static uint OnMessage(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* user
    )
    {
        string message = $"Vulkan {types}: {CString.Read(data->pMessage)}";
        if (severity.HasFlag(VkDebugUtilsMessageSeverityFlagsEXT.Error))
            Log.Error(message);
        else if (severity.HasFlag(VkDebugUtilsMessageSeverityFlagsEXT.Warning))
            Log.Warn(message);
        else
            Log.Verbose(message);

        return 0;
    }

    private static bool HasLayer(string name)
    {
        vkEnumerateInstanceLayerProperties(out uint count);
        VkLayerProperties[] layers = new VkLayerProperties[count];
        vkEnumerateInstanceLayerProperties(layers);

        return layers.Any(layer => CString.Read(layer.layerName) == name);
    }

    private static HashSet<string> GetInstanceExtensions()
    {
        vkEnumerateInstanceExtensionProperties(out uint count);
        VkExtensionProperties[] extensions = new VkExtensionProperties[count];
        vkEnumerateInstanceExtensionProperties(extensions);

        return GetNames(extensions);
    }

    private static HashSet<string> GetNames(VkExtensionProperties[] extensions) =>
        extensions.Select(extension => CString.Read(extension.extensionName)).ToHashSet(StringComparer.Ordinal);

    private static VkDebugUtilsMessengerCreateInfoEXT GetMessengerInfo() =>
        new()
        {
            messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Error | VkDebugUtilsMessageSeverityFlagsEXT.Warning,
            messageType =
                VkDebugUtilsMessageTypeFlagsEXT.Validation
                | VkDebugUtilsMessageTypeFlagsEXT.Performance
                | VkDebugUtilsMessageTypeFlagsEXT.General,
            pfnUserCallback = &OnMessage,
        };

    private static string GetKindName(VkPhysicalDeviceType kind) =>
        kind switch
        {
            VkPhysicalDeviceType.DiscreteGpu => "discrete",
            VkPhysicalDeviceType.IntegratedGpu => "integrated",
            VkPhysicalDeviceType.VirtualGpu => "virtual",
            VkPhysicalDeviceType.Cpu => "software",
            VkPhysicalDeviceType.Other => "other",
            _ => "other",
        };

    /// <summary>
    /// Whether the drivers the loader takes now give a GPU, asked through an instance made for it and freed.
    /// </summary>
    private static bool HasGpu()
    {
        VkApplicationInfo application = new() { apiVersion = VkVersion.Version_1_3 };
        VkInstanceCreateInfo createInfo = new() { pApplicationInfo = &application };
        if (vkCreateInstance(&createInfo, out VkInstance probe) != VkResult.Success)
            return false;

        VkInstanceApi api = new(probe);
        api.vkEnumeratePhysicalDevices(out uint gpus);
        api.vkDestroyInstance();

        return gpus > 0;
    }

    private VkInstance CreateInstance(List<string> extensions, bool hasPortability)
    {
        using NativeStrings extensionNames = new(extensions);
        using NativeStrings layerNames = new(HasValidation ? [ValidationLayer] : []);
        fixed (byte* applicationName = "Magic\0"u8)
        {
            VkApplicationInfo application = new()
            {
                pApplicationName = applicationName,
                pEngineName = applicationName,
                apiVersion = VkVersion.Version_1_3,
            };
            VkDebugUtilsMessengerCreateInfoEXT messenger = GetMessengerInfo();
            VkInstanceCreateInfo createInfo = new()
            {
                pApplicationInfo = &application,
                flags = hasPortability ? VkInstanceCreateFlags.EnumeratePortabilityKHR : VkInstanceCreateFlags.None,
                enabledExtensionCount = extensionNames.Count,
                ppEnabledExtensionNames = extensionNames.Pointers,
                enabledLayerCount = layerNames.Count,
                ppEnabledLayerNames = layerNames.Pointers,
                // The messenger in the chain catches what the instance's own creation reports.
                pNext = HasValidation && HasDebugUtils ? &messenger : null,
            };
            vkCreateInstance(&createInfo, out VkInstance instance).ThrowIfFailed("an instance");

            return instance;
        }
    }

    private VkDebugUtilsMessengerEXT CreateMessenger()
    {
        VkDebugUtilsMessengerCreateInfoEXT createInfo = GetMessengerInfo();
        VkResult result = InstanceApi.vkCreateDebugUtilsMessengerEXT(
            &createInfo,
            out VkDebugUtilsMessengerEXT messenger
        );
        if (result != VkResult.Success)
            Log.Warn($"Vulkan's validation messages are not read: {result}.");

        return messenger;
    }

    /// <summary>
    /// The GPU to use: among those with the baseline that can show the window, the first whose name holds
    /// <paramref name="named"/> when it isn't empty; else a discrete one, else an integrated, else whatever draws, a
    /// software device last.
    /// </summary>
    private Pick PickPhysicalDevice(string named)
    {
        InstanceApi.vkEnumeratePhysicalDevices(out uint count);
        Span<VkPhysicalDevice> devices = new VkPhysicalDevice[count];
        InstanceApi.vkEnumeratePhysicalDevices(devices);
        List<Pick> usable = [];
        List<string> refused = [];
        foreach (VkPhysicalDevice device in devices)
        {
            Result<Pick> examined = Examine(device);
            if (examined.Ok)
                usable.Add(examined.Value);
            else
                refused.Add(examined.Error);
        }

        if (usable.Count == 0)
        {
            throw new InvalidOperationException(
                $"No GPU has what Magic needs: Vulkan 1.3 with bindless descriptors and dynamic rendering. "
                    + string.Join(" ", refused)
            );
        }

        if (named.Length == 0)
            return usable.MaxBy(pick => pick.Score);

        int found = usable.FindIndex(pick =>
            pick.Limits.DeviceName.Contains(named, StringComparison.OrdinalIgnoreCase)
        );
        if (found >= 0)
            return usable[found];

        string names = string.Join(", ", usable.Select(pick => pick.Limits.DeviceName));
        Log.Warn($"Rendering.Gpu {named} names none of the GPUs that can draw ({names}), so the fastest draws.");

        return usable.MaxBy(pick => pick.Score);
    }

    [SuppressMessage(
        "Style",
        "IDE0045:Convert to conditional expression",
        Justification = "Each refusal reads as one line, which a chain of conditionals would not."
    )]
    private Result<Pick> Examine(VkPhysicalDevice device)
    {
        VkPhysicalDeviceRayQueryFeaturesKHR rayQuery = new();
        VkPhysicalDeviceAccelerationStructureFeaturesKHR acceleration = new() { pNext = &rayQuery };
        VkPhysicalDeviceVulkan13Features features13 = new() { pNext = &acceleration };
        VkPhysicalDeviceVulkan12Features features12 = new() { pNext = &features13 };
        VkPhysicalDeviceVulkan11Features features11 = new() { pNext = &features12 };
        VkPhysicalDeviceFeatures2 features = new() { pNext = &features11 };
        InstanceApi.vkGetPhysicalDeviceFeatures2(device, &features);
        VkPhysicalDeviceVulkan12Properties properties12 = new();
        VkPhysicalDeviceProperties2 properties = new() { pNext = &properties12 };
        InstanceApi.vkGetPhysicalDeviceProperties2(device, &properties);
        string name = CString.Read(properties.properties.deviceName);
        HashSet<string> extensions = GetDeviceExtensions(device);

        if (properties.properties.apiVersion < VkVersion.Version_1_3)
            return Result.Failure($"{name} has Vulkan {properties.properties.apiVersion}, below 1.3.");

        VkPhysicalDeviceFeatures core = features.features;
        if (
            !core.samplerAnisotropy
            || !core.multiDrawIndirect
            || !core.drawIndirectFirstInstance
            || !core.fillModeNonSolid
            || !core.fragmentStoresAndAtomics
            || !core.shaderClipDistance
            || !core.shaderStorageImageReadWithoutFormat
            || !core.shaderStorageImageWriteWithoutFormat
            || !core.textureCompressionBC
        )
        {
            return Result.Failure($"{name} lacks a Vulkan 1.0 feature the renderer needs.");
        }

        if (!features11.shaderDrawParameters)
            return Result.Failure($"{name} lacks shaderDrawParameters.");

        if (
            !features12.descriptorIndexing
            || !features12.runtimeDescriptorArray
            || !features12.shaderSampledImageArrayNonUniformIndexing
            || !features12.shaderStorageBufferArrayNonUniformIndexing
            || !features12.shaderStorageImageArrayNonUniformIndexing
            || !features12.descriptorBindingSampledImageUpdateAfterBind
            || !features12.descriptorBindingStorageImageUpdateAfterBind
            || !features12.descriptorBindingStorageBufferUpdateAfterBind
            || !features12.descriptorBindingPartiallyBound
            || !features12.descriptorBindingUpdateUnusedWhilePending
            || !features12.bufferDeviceAddress
            || !features12.drawIndirectCount
            || !features12.timelineSemaphore
            || !features12.hostQueryReset
            || !features12.scalarBlockLayout
        )
        {
            return Result.Failure($"{name} lacks a Vulkan 1.2 feature the renderer needs: the bindless heap.");
        }

        bool has13 =
            features13.dynamicRendering
            && features13.synchronization2
            && features13.maintenance4
            && features13.shaderDemoteToHelperInvocation;
        if (!has13)
            return Result.Failure($"{name} lacks dynamic rendering, synchronization2 or demote to helper invocation.");

        uint queueFamily = FindQueueFamily(device);
        if (queueFamily == uint.MaxValue)
            return Result.Failure($"{name} has no queue that draws, computes and shows the window.");

        if (Surface.IsNotNull && !extensions.Contains(SwapchainExtension))
            return Result.Failure($"{name} cannot show a window.");

        VkPhysicalDeviceLimits limits = properties.properties.limits;
        VkPhysicalDeviceType kind = properties.properties.deviceType;
        bool hasRayQuery =
            extensions.Contains(RayQueryExtension)
            && extensions.Contains(AccelerationStructureExtension)
            && extensions.Contains(DeferredHostOperationsExtension)
            && rayQuery.rayQuery
            && acceleration.accelerationStructure
            && acceleration.descriptorBindingAccelerationStructureUpdateAfterBind;

        return Result.Success(
            new Pick
            {
                Device = device,
                QueueFamily = queueFamily,
                Score = kind switch
                {
                    VkPhysicalDeviceType.DiscreteGpu => 4,
                    VkPhysicalDeviceType.IntegratedGpu => 3,
                    VkPhysicalDeviceType.VirtualGpu => 2,
                    VkPhysicalDeviceType.Cpu => 0,
                    VkPhysicalDeviceType.Other => 1,
                    _ => 1,
                },
                ApiVersion = properties.properties.apiVersion,
                Kind = kind,
                Driver = $"{CString.Read(properties12.driverName)} {CString.Read(properties12.driverInfo)}",
                TimestampPeriod = limits.timestampPeriod,
                Extensions = extensions,
                Features = new GpuFeatures { HasRayQuery = hasRayQuery },
                Limits = new GpuLimits
                {
                    DeviceName = name,
                    UploadAlignment = (int)Math.Max(limits.optimalBufferCopyOffsetAlignment, 16),
                    DeviceMemoryBytes = GetDeviceMemoryBytes(device),
                },
            }
        );
    }

    private HashSet<string> GetDeviceExtensions(VkPhysicalDevice device)
    {
        InstanceApi.vkEnumerateDeviceExtensionProperties(device, out uint count);
        VkExtensionProperties[] extensions = new VkExtensionProperties[count];
        InstanceApi.vkEnumerateDeviceExtensionProperties(device, extensions);

        return GetNames(extensions);
    }

    /// <summary>
    /// A queue family that draws and computes, and shows the window when there is one; <see cref="uint.MaxValue"/>
    /// when none.
    /// </summary>
    private uint FindQueueFamily(VkPhysicalDevice device)
    {
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, out uint count);
        Span<VkQueueFamilyProperties> families = new VkQueueFamilyProperties[count];
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, families);
        for (uint i = 0; i < count; i++)
        {
            VkQueueFlags flags = families[(int)i].queueFlags;
            if (!flags.HasFlag(VkQueueFlags.Graphics) || !flags.HasFlag(VkQueueFlags.Compute))
                continue;

            if (Surface.IsNull)
                return i;

            InstanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(device, i, Surface, out VkBool32 canPresent);
            if (canPresent)
                return i;
        }

        return uint.MaxValue;
    }

    private long GetDeviceMemoryBytes(VkPhysicalDevice device)
    {
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(device, out VkPhysicalDeviceMemoryProperties memory);
        long bytes = 0;
        for (int i = 0; i < memory.memoryHeapCount; i++)
        {
            if (memory.memoryHeaps[i].flags.HasFlag(VkMemoryHeapFlags.DeviceLocal))
                bytes += (long)memory.memoryHeaps[i].size;
        }

        return bytes;
    }

    private VkDevice CreateDevice(in Pick pick)
    {
        List<string> extensions = [];
        if (Surface.IsNotNull)
            extensions.Add(SwapchainExtension);

        if (pick.Extensions.Contains(PortabilitySubsetExtension))
            extensions.Add(PortabilitySubsetExtension);

        if (pick.Features.HasRayQuery)
            extensions.AddRange([RayQueryExtension, AccelerationStructureExtension, DeferredHostOperationsExtension]);

        if (HasMemoryBudget)
            extensions.Add(MemoryBudgetExtension);

        using NativeStrings extensionNames = new(extensions);
        VkPhysicalDeviceRayQueryFeaturesKHR rayQuery = new() { rayQuery = true };
        VkPhysicalDeviceAccelerationStructureFeaturesKHR acceleration = new()
        {
            accelerationStructure = true,
            descriptorBindingAccelerationStructureUpdateAfterBind = true,
        };
        VkPhysicalDeviceVulkan13Features features13 = new()
        {
            dynamicRendering = true,
            synchronization2 = true,
            maintenance4 = true,
            shaderDemoteToHelperInvocation = true,
        };
        VkPhysicalDeviceVulkan12Features features12 = new()
        {
            pNext = &features13,
            descriptorIndexing = true,
            runtimeDescriptorArray = true,
            shaderSampledImageArrayNonUniformIndexing = true,
            shaderStorageBufferArrayNonUniformIndexing = true,
            shaderStorageImageArrayNonUniformIndexing = true,
            descriptorBindingSampledImageUpdateAfterBind = true,
            descriptorBindingStorageImageUpdateAfterBind = true,
            descriptorBindingStorageBufferUpdateAfterBind = true,
            descriptorBindingPartiallyBound = true,
            descriptorBindingUpdateUnusedWhilePending = true,
            bufferDeviceAddress = true,
            drawIndirectCount = true,
            timelineSemaphore = true,
            hostQueryReset = true,
            scalarBlockLayout = true,
        };
        VkPhysicalDeviceVulkan11Features features11 = new() { pNext = &features12, shaderDrawParameters = true };
        VkPhysicalDeviceFeatures2 features = new()
        {
            pNext = &features11,
            features = new VkPhysicalDeviceFeatures
            {
                samplerAnisotropy = true,
                multiDrawIndirect = true,
                drawIndirectFirstInstance = true,
                fillModeNonSolid = true,
                fragmentStoresAndAtomics = true,
                shaderClipDistance = true,
                shaderStorageImageReadWithoutFormat = true,
                shaderStorageImageWriteWithoutFormat = true,
                textureCompressionBC = true,
            },
        };

        // The optional features chain in after the baseline, each only when the GPU has it.
        void* chain = &features;
        if (pick.Features.HasRayQuery)
        {
            rayQuery.pNext = chain;
            acceleration.pNext = &rayQuery;
            chain = &acceleration;
        }

        float priority = 1;
        VkDeviceQueueCreateInfo queue = new()
        {
            queueFamilyIndex = pick.QueueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        VkDeviceCreateInfo createInfo = new()
        {
            pNext = chain,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queue,
            enabledExtensionCount = extensionNames.Count,
            ppEnabledExtensionNames = extensionNames.Pointers,
        };
        InstanceApi
            .vkCreateDevice(pick.Device, &createInfo, out VkDevice device)
            .ThrowIfFailed($"a device on {pick.Limits.DeviceName}");

        return device;
    }

    /// <summary>
    /// A GPU that could be used, with what was read of it.
    /// </summary>
    private readonly record struct Pick
    {
        public required VkPhysicalDevice Device { get; init; }

        public required uint QueueFamily { get; init; }

        public required int Score { get; init; }

        public required VkVersion ApiVersion { get; init; }

        public required VkPhysicalDeviceType Kind { get; init; }

        /// <summary>
        /// The driver's name and version, as it gives them.
        /// </summary>
        public required string Driver { get; init; }

        public required float TimestampPeriod { get; init; }

        public required HashSet<string> Extensions { get; init; }

        public required GpuFeatures Features { get; init; }

        public required GpuLimits Limits { get; init; }
    }
}

/// <summary>
/// What the GPU is and holds: its name, the alignment an upload's offset takes and its device memory.
/// </summary>
internal readonly record struct GpuLimits
{
    public required string DeviceName { get; init; }

    /// <summary>
    /// Bytes an upload's offset is a multiple of.
    /// </summary>
    public required int UploadAlignment { get; init; }

    /// <summary>
    /// Bytes of device memory the GPU has for the heap's resources.
    /// </summary>
    public required long DeviceMemoryBytes { get; init; }
}
