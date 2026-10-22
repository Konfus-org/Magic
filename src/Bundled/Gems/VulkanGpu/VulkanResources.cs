using Magic.Contexts.Gpu;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// What a buffer or one level of one layer of a texture was last used for, as the barriers before a pass read it: its
/// image layout, the stages that last wrote it and how, the stages that read it since, and those the write was made
/// visible to. A read waits for the write only in a stage it was not yet made visible to; a write waits for every
/// access since the last.
/// </summary>
internal struct ResourceState
{
    internal VkImageLayout Layout { get; set; }

    internal VkPipelineStageFlags2 WriteStages { get; set; }

    internal VkAccessFlags2 WriteAccess { get; set; }

    internal VkPipelineStageFlags2 ReadStages { get; set; }

    internal VkPipelineStageFlags2 VisibleStages { get; set; }
}

/// <summary>
/// A buffer and its memory, mapped for good when the CPU writes or reads it.
/// </summary>
internal sealed unsafe class VulkanBuffer
{
    private ResourceState _state;

    internal VulkanBuffer(VkBuffer handle, VkDeviceMemory memory, ulong size, byte* mapped)
    {
        Handle = handle;
        Memory = memory;
        Size = size;
        Mapped = mapped;
    }

    internal VkBuffer Handle { get; }

    internal VkDeviceMemory Memory { get; }

    internal ulong Size { get; }

    internal byte* Mapped { get; }

    internal ref ResourceState State => ref _state;
}

/// <summary>
/// A texture and its memory and views: the whole of it as shaders sample it, each level of each layer (or depth slice)
/// as a render target, and each level of each layer as compute writes it. A window's render target is one too, with no
/// image of its own: its image is the one the window acquired this frame.
/// <para>
/// What it was last used for is one state while every level of every layer shares it, as a texture sampled whole does,
/// and one per level of each layer once a pass uses one alone (a render target, a mip made from the one before): a
/// texture of thousands of layers costs one state, not thousands, each pass that samples it.
/// </para>
/// </summary>
internal sealed class VulkanTexture
{
    private ResourceState _whole;

    internal VulkanTexture(
        in GpuTextureDescription description,
        VkImage image,
        VkDeviceMemory memory,
        VulkanWindow? window = null)
    {
        Description = description;
        Image = image;
        Memory = memory;
        Window = window;
        Layers = GetLayers(description);
        States = new ResourceState[description.Levels * Layers];
    }

    internal GpuTextureDescription Description { get; }

    internal GpuFormat Format => Window?.Format ?? Description.Format;

    internal VkImage Image { get; }

    internal VkDeviceMemory Memory { get; }

    /// <summary>
    /// The window this is the render target of; null for a texture.
    /// </summary>
    internal VulkanWindow? Window { get; }

    internal uint Layers { get; }

    /// <summary>
    /// Each level of each layer's state, level-major within a layer, while <see cref="IsWhole"/> is false:
    /// <see cref="GetState"/> finds one.
    /// </summary>
    internal ResourceState[] States { get; }

    /// <summary>
    /// Whether every level of every layer was last used alike, so <see cref="Whole"/> says it for all.
    /// </summary>
    internal bool IsWhole { get; set; } = true;

    internal ref ResourceState Whole => ref _whole;

    internal VkImageView SampledView { get; set; }

    /// <summary>
    /// Per level, then per layer or depth slice, as a 2D render target.
    /// </summary>
    internal VkImageView[] TargetViews { get; set; } = [];

    /// <summary>
    /// Per level, then per layer: what compute writes, a level of a 3D texture whole.
    /// </summary>
    internal VkImageView[] StorageViews { get; set; } = [];

    /// <summary>
    /// Array layers a texture so described has: six for a cube, one for a 3D texture, whose depth is one layer's.
    /// </summary>
    internal static uint GetLayers(in GpuTextureDescription description) => description.Kind switch
    {
        GpuTextureKind.Cube => 6,
        GpuTextureKind.Texture3D => 1,
        GpuTextureKind.Texture2D or GpuTextureKind.Texture2DArray => description.Layers,
        _ => description.Layers,
    };

    /// <summary>
    /// The array layer a level of a layer or depth slice is in, which it is tracked, copied and written by: a 3D
    /// texture's level is one layer whole, whatever slice is named.
    /// </summary>
    internal uint GetArrayLayer(uint layer) => Description.Kind == GpuTextureKind.Texture3D ? 0 : layer;

    internal ref ResourceState GetState(uint level, uint layer) => ref States[(layer * Description.Levels) + level];

    /// <summary>
    /// A level's size, the whole texture's at level 0.
    /// </summary>
    internal (uint Width, uint Height, uint Depth) GetSize(uint level)
    {
        uint depth = Description.Kind == GpuTextureKind.Texture3D ? Description.Layers : 1;
        return (Math.Max(1, Description.Width >> (int)level), Math.Max(1, Description.Height >> (int)level),
            Math.Max(1, depth >> (int)level));
    }

    /// <summary>
    /// Where the render target view of <paramref name="level"/> and <paramref name="layer"/> is: a 3D texture's
    /// levels each have as many as that level has slices.
    /// </summary>
    internal int GetTargetIndex(uint level, uint layer)
    {
        if (Description.Kind != GpuTextureKind.Texture3D) return (int)((level * Layers) + layer);

        int index = 0;
        for (uint before = 0; before < level; before++)
        {
            index += (int)GetSize(before).Depth;
        }

        return index + (int)layer;
    }
}

/// <summary>
/// The GPU's objects by the ids <see cref="Interfaces.IGpu"/> hands out: buffers, textures and samplers, each
/// with memory of its own, as the few hundred a renderer makes are far under the GPU's limit on allocations. What is
/// released goes once the frames that could still use it are done.
/// </summary>
internal sealed unsafe class VulkanResources(VulkanDevice device) : IDisposable
{
    private readonly VulkanDevice _device = device;

    internal Table<VulkanBuffer> Buffers { get; } = new();

    internal Table<VulkanTexture> Textures { get; } = new();

    internal Table<VkSampler> Samplers { get; } = new();

    internal Table<VulkanPipeline> Pipelines { get; } = new();

    /// <summary>
    /// Destroys every buffer, texture and sampler still alive, the pipelines being their maker's: the GPU is idle by
    /// now.
    /// </summary>
    public void Dispose()
    {
        foreach (VulkanBuffer buffer in Buffers.Live)
        {
            Destroy(buffer);
        }

        foreach (VulkanTexture texture in Textures.Live)
        {
            Destroy(texture);
        }

        foreach (VkSampler sampler in Samplers.Live)
        {
            _device.Api.vkDestroySampler(sampler);
        }
    }

    /// <summary>
    /// A buffer with memory of its own: on the GPU, or mapped for the CPU when <paramref name="isHost"/>, as staging and
    /// read backs are.
    /// </summary>
    internal VulkanBuffer CreateBuffer(ulong bytes, VkBufferUsageFlags usage, bool isHost)
    {
        VkBufferCreateInfo info = new()
        {
            size = Math.Max(bytes, 4),
            usage = usage,
            sharingMode = VkSharingMode.Exclusive
        };
        VkBuffer buffer;
        Check(_device.Api.vkCreateBuffer(&info, &buffer), $"a {bytes}-byte buffer");

        _device.Api.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements needs);
        VkMemoryPropertyFlags flags = isHost
            ? VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent
            : VkMemoryPropertyFlags.DeviceLocal;
        VkDeviceMemory memory = Allocate(needs, flags, $"a {bytes}-byte buffer");
        _device.Api.vkBindBufferMemory(buffer, memory, 0);
        void* mapped = null;
        if (isHost)
        {
            Check(
            _device.Api.vkMapMemory(memory, 0, Vulkan.VK_WHOLE_SIZE, VkMemoryMapFlags.None, &mapped),
            "staging memory");
        }

        return new VulkanBuffer(buffer, memory, info.size, (byte*)mapped);
    }

    /// <summary>
    /// A texture with memory of its own on the GPU and every view its usage needs.
    /// </summary>
    internal VulkanTexture CreateTexture(in GpuTextureDescription description)
    {
        uint layers = VulkanTexture.GetLayers(description);
        VkImageCreateFlags flags = description.Kind switch
        {
            GpuTextureKind.Cube => VkImageCreateFlags.CubeCompatible,
            GpuTextureKind.Texture3D => VkImageCreateFlags.Array2DCompatible,
            GpuTextureKind.Texture2D or GpuTextureKind.Texture2DArray => VkImageCreateFlags.None,
            _ => VkImageCreateFlags.None,
        };
        VkImageCreateInfo info = new()
        {
            flags = flags,
            imageType = description.Kind.ToVulkan(),
            format = description.Format.ToVulkan(),
            extent = new VkExtent3D(
                description.Width,
                description.Height,
                description.Kind == GpuTextureKind.Texture3D ? description.Layers : 1),
            mipLevels = description.Levels,
            arrayLayers = layers,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = description.Usage.ToVulkan(),
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        string what = $"a {description.Width} x {description.Height} {description.Format} texture";
        VkImage image;
        Check(_device.Api.vkCreateImage(&info, &image), what);

        _device.Api.vkGetImageMemoryRequirements(image, out VkMemoryRequirements needs);
        VkDeviceMemory memory = Allocate(needs, VkMemoryPropertyFlags.DeviceLocal, what);
        _device.Api.vkBindImageMemory(image, memory, 0);

        VulkanTexture texture = new(description, image, memory);
        VkImageUsageFlags usage = info.usage;
        if ((usage & VkImageUsageFlags.Sampled) != VkImageUsageFlags.None)
        {
            texture.SampledView =
                CreateView(texture, description.Kind.ToSampledView(), 0, description.Levels, 0, layers);
        }

        if ((usage & (VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.DepthStencilAttachment))
            != VkImageUsageFlags.None)
        {
            texture.TargetViews = CreateTargetViews(texture);
        }

        if ((usage & VkImageUsageFlags.Storage)
            != VkImageUsageFlags.None)
        {
            texture.StorageViews = CreateStorageViews(texture);
        }

        return texture;
    }

    internal VkSampler CreateSampler(in GpuSamplerDescription description)
    {
        bool isAnisotropic = _device.HasAnisotropy && description.Anisotropy > 1;
        VkSamplerCreateInfo info = new()
        {
            magFilter = description.Filter.ToVulkan(),
            minFilter = description.Filter.ToVulkan(),
            mipmapMode = description.Filter.ToVulkanMipmaps(),
            addressModeU = description.Wrap.ToVulkan(),
            addressModeV = description.Wrap.ToVulkan(),
            addressModeW = description.Wrap.ToVulkan(),
            anisotropyEnable = isAnisotropic,
            maxAnisotropy = isAnisotropic ? Math.Min(description.Anisotropy, _device.Limits.maxSamplerAnisotropy) : 1,
            compareEnable = description.Compare is not null,
            compareOp = (description.Compare ?? GpuCompare.Never).ToVulkan(),
            minLod = 0,
            maxLod = Vulkan.VK_LOD_CLAMP_NONE,
        };
        VkSampler sampler;
        Check(_device.Api.vkCreateSampler(&info, &sampler), "a sampler");
        return sampler;
    }

    /// <summary>
    /// Destroys what a frame released, once the GPU is done with every frame that could use it.
    /// </summary>
    internal void Destroy(VulkanBuffer buffer)
    {
        if (buffer.Mapped != null) _device.Api.vkUnmapMemory(buffer.Memory);

        _device.Api.vkDestroyBuffer(buffer.Handle);
        _device.Api.vkFreeMemory(buffer.Memory);
    }

    internal void Destroy(VulkanTexture texture)
    {
        if (texture.Window is not null) return;

        if (texture.SampledView.IsNotNull) _device.Api.vkDestroyImageView(texture.SampledView);

        foreach (VkImageView view in texture.TargetViews)
        {
            _device.Api.vkDestroyImageView(view);
        }

        foreach (VkImageView view in texture.StorageViews)
        {
            _device.Api.vkDestroyImageView(view);
        }

        _device.Api.vkDestroyImage(texture.Image);
        _device.Api.vkFreeMemory(texture.Memory);
    }

    /// <summary>
    /// Throws a failure to make <paramref name="what"/>: a caller's mistake or the machine out of memory.
    /// </summary>
    private static void Check(VkResult result, string what)
    {
        if (result == VkResult.Success) return;

        throw new InvalidOperationException($"The GPU could not make {what}: {result}.");
    }

    private VkDeviceMemory Allocate(in VkMemoryRequirements needs, VkMemoryPropertyFlags flags, string what)
    {
        VkMemoryAllocateInfo info = new()
        {
            allocationSize = needs.size,
            memoryTypeIndex = _device.FindMemoryType(needs.memoryTypeBits, flags),
        };
        VkDeviceMemory memory;
        Check(_device.Api.vkAllocateMemory(&info, &memory), what);
        return memory;
    }

    private VkImageView CreateView(
        VulkanTexture texture,
        VkImageViewType type,
        uint level,
        uint levels,
        uint layer,
        uint layers)
    {
        VkImageViewCreateInfo info = new()
        {
            image = texture.Image,
            viewType = type,
            format = texture.Description.Format.ToVulkan(),
            subresourceRange = new VkImageSubresourceRange(
                texture.Description.Format.ToAspect(),
                level,
                levels,
                layer,
                layers),
        };
        VkImageView view;
        Check(_device.Api.vkCreateImageView(&info, &view), "a texture view");
        return view;
    }

    /// <summary>
    /// A 2D view of each level of each layer, or of each depth slice of a 3D texture's levels, as a pass draws into one.
    /// </summary>
    private VkImageView[] CreateTargetViews(VulkanTexture texture)
    {
        List<VkImageView> views = [];
        for (uint level = 0; level < texture.Description.Levels; level++)
        {
            uint count = texture.Description.Kind == GpuTextureKind.Texture3D
                ? texture.GetSize(level).Depth
                : texture.Layers;
            for (uint layer = 0; layer < count; layer++)
            {
                views.Add(CreateView(texture, VkImageViewType.Image2D, level, 1, layer, 1));
            }
        }

        return [.. views];
    }

    /// <summary>
    /// A view of each level of each layer as compute writes it: a 3D texture's level whole, as a 3D storage image.
    /// </summary>
    private VkImageView[] CreateStorageViews(VulkanTexture texture)
    {
        VkImageViewType type = texture.Description.Kind == GpuTextureKind.Texture3D
            ? VkImageViewType.Image3D
            : VkImageViewType.Image2D;
        VkImageView[] views = new VkImageView[texture.Description.Levels * texture.Layers];
        for (uint layer = 0; layer < texture.Layers; layer++)
        {
            for (uint level = 0; level < texture.Description.Levels; level++)
            {
                views[(layer * texture.Description.Levels) + level] = CreateView(texture, type, level, 1, layer, 1);
            }
        }

        return views;
    }
}

/// <summary>
/// Objects by handle id: dense, id 0 never used, freed ids used again.
/// </summary>
internal sealed class Table<T>
{
    private readonly List<T?> _items = [default];
    private readonly List<bool> _isLive = [false];
    private readonly Stack<uint> _free = [];

    internal IEnumerable<T> Live
    {
        get
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_isLive[i] && _items[i] is { } item) yield return item;
            }
        }
    }

    /// <summary>
    /// What <paramref name="id"/> holds; the default for an id that holds nothing.
    /// </summary>
    internal T? this[uint id] => id < _items.Count && _isLive[(int)id] ? _items[(int)id] : default;

    internal uint Add(T item)
    {
        if (_free.TryPop(out uint id))
        {
            _items[(int)id] = item;
            _isLive[(int)id] = true;
            return id;
        }

        _items.Add(item);
        _isLive.Add(true);
        return (uint)(_items.Count - 1);
    }

    /// <summary>
    /// Forgets <paramref name="id"/>, handing back what it held.
    /// </summary>
    internal T? Free(uint id)
    {
        if (id == 0 || id >= _items.Count || !_isLive[(int)id]) return default;

        T? item = _items[(int)id];
        _items[(int)id] = default;
        _isLive[(int)id] = false;
        _free.Push(id);
        return item;
    }
}
