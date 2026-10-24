using Magic.Contexts.Gpu;
using Magic.Services;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// The heap: one descriptor set holding every buffer, texture and sampler the GPU made, each at its handle's id in the
/// array for its kind, as Shaders/Core/Heap.slang declares them; and the one pipeline layout of every pipeline, that
/// set and a push constant block. A resource's descriptors are written as it is made, and its slot is written again
/// only once its id is handed out again, which waits until no frame in flight can read it, so the set is never written
/// where a frame still reads it.
/// </summary>
internal sealed unsafe class VulkanHeap : IDisposable
{
    /// <summary>
    /// The levels each texture has room for in the arrays of levels: a texture's level is at its id times this, plus
    /// the level. Heap.slang's LevelsPerTexture.
    /// </summary>
    internal const uint LevelsPerTexture = 16;

    /// <summary>
    /// The bytes of a pipeline's constant block: what every Vulkan device pushes.
    /// </summary>
    internal const uint ConstantBytes = 128;

    private const uint BufferBinding = 0;
    private const uint TextureBinding = 1;
    private const uint TextureArrayBinding = 2;
    private const uint CubeBinding = 3;
    private const uint VolumeBinding = 4;
    private const uint UintTextureBinding = 5;
    private const uint LevelBinding = 6;
    private const uint ArrayLevelBinding = 7;
    private const uint VolumeLevelBinding = 8;
    private const uint UintLevelBinding = 9;
    private const uint SamplerBinding = 10;
    private const uint CompareSamplerBinding = 11;
    private const int BindingCount = 12;

    private const uint WantedBuffers = 16384;
    private const uint WantedTextures = 8192;
    private const uint WantedSamplers = 256;

    private readonly VulkanDevice _device;
    private readonly VkDescriptorSetLayout _layout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet _set;

    /// <summary>
    /// Ids past what the heap has room for whose resources were made anyway, told about once each kind.
    /// </summary>
    private bool _isBufferOverflowTold;
    private bool _isTextureOverflowTold;
    private bool _isSamplerOverflowTold;

    public VulkanHeap(VulkanDevice device)
    {
        _device = device;
        VkPhysicalDeviceVulkan12Properties limits = device.DescriptorLimits;

        // As many textures as the device lets every array of them hold: five arrays of them sampled, and four of their
        // levels written.
        Textures = Math.Min(
            WantedTextures,
            Math.Min(
                limits.maxDescriptorSetUpdateAfterBindSampledImages / 5,
                limits.maxDescriptorSetUpdateAfterBindStorageImages / (4 * LevelsPerTexture)));
        Buffers = Math.Min(WantedBuffers, limits.maxDescriptorSetUpdateAfterBindStorageBuffers);
        Samplers = Math.Min(WantedSamplers, limits.maxDescriptorSetUpdateAfterBindSamplers / 2);
        (VkDescriptorType Type, uint Count)[] bindings =
        [
            (VkDescriptorType.StorageBuffer, Buffers),
            (VkDescriptorType.SampledImage, Textures),
            (VkDescriptorType.SampledImage, Textures),
            (VkDescriptorType.SampledImage, Textures),
            (VkDescriptorType.SampledImage, Textures),
            (VkDescriptorType.SampledImage, Textures),
            (VkDescriptorType.StorageImage, Textures * LevelsPerTexture),
            (VkDescriptorType.StorageImage, Textures * LevelsPerTexture),
            (VkDescriptorType.StorageImage, Textures * LevelsPerTexture),
            (VkDescriptorType.StorageImage, Textures * LevelsPerTexture),
            (VkDescriptorType.Sampler, Samplers),
            (VkDescriptorType.Sampler, Samplers),
        ];
        _layout = CreateLayout(bindings);
        _pool = CreatePool(bindings);
        _set = Allocate();
        PipelineLayout = CreatePipelineLayout();
    }

    internal uint Buffers { get; }

    internal uint Textures { get; }

    internal uint Samplers { get; }

    /// <summary>
    /// The layout every pipeline is made with: the heap at set 0, and a push constant block every stage sees.
    /// </summary>
    internal VkPipelineLayout PipelineLayout { get; }

    public void Dispose()
    {
        _device.Api.vkDestroyPipelineLayout(PipelineLayout);
        _device.Api.vkDestroyDescriptorPool(_pool);
        _device.Api.vkDestroyDescriptorSetLayout(_layout);
    }

    /// <summary>
    /// Binds the heap for the pipelines of <paramref name="bindPoint"/> recorded after it.
    /// </summary>
    internal void Bind(VkCommandBuffer commandBuffer, VkPipelineBindPoint bindPoint)
    {
        VkDescriptorSet set = _set;
        _device.Api.vkCmdBindDescriptorSets(commandBuffer, bindPoint, PipelineLayout, 0, 1, &set);
    }

    /// <summary>
    /// Writes a buffer shaders read or write into the heap at <paramref name="id"/>.
    /// </summary>
    internal void Write(uint id, VulkanBuffer buffer, VkBufferUsageFlags usage)
    {
        if ((usage & VkBufferUsageFlags.StorageBuffer) == VkBufferUsageFlags.None) return;

        if (id >= Buffers)
        {
            TellOverflow(ref _isBufferOverflowTold, "buffers", Buffers);

            return;
        }

        VkDescriptorBufferInfo info = new() { buffer = buffer.Handle, offset = 0, range = Vulkan.VK_WHOLE_SIZE };
        VkWriteDescriptorSet write = new()
        {
            dstSet = _set,
            dstBinding = BufferBinding,
            dstArrayElement = id,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageBuffer,
            pBufferInfo = &info,
        };
        _device.Api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    /// <summary>
    /// Writes a texture into the heap at <paramref name="id"/>: its whole, sampled, in the array for its kind, and each
    /// level compute writes, at the id's levels in the array of levels for its kind.
    /// </summary>
    internal void Write(uint id, VulkanTexture texture)
    {
        if (id >= Textures)
        {
            TellOverflow(ref _isTextureOverflowTold, "textures", Textures);

            return;
        }

        bool isUint = texture.Description.Format == GpuFormat.R32Uint;
        GpuTextureKind kind = texture.Description.Kind;
        List<(uint Binding, uint Element, VkDescriptorType Type, VkDescriptorImageInfo Image)> writes = [];
        if (texture.SampledView.IsNotNull)
        {
            uint? binding = (kind, isUint) switch
            {
                (GpuTextureKind.Texture2D, true) => UintTextureBinding,
                (GpuTextureKind.Texture2D, false) => TextureBinding,
                (GpuTextureKind.Texture2DArray, false) => TextureArrayBinding,
                (GpuTextureKind.Cube, false) => CubeBinding,
                (GpuTextureKind.Texture3D, false) => VolumeBinding,
                _ => null,
            };
            if (binding is { } sampled)
            {
                writes.Add((
                    sampled,
                    id,
                    VkDescriptorType.SampledImage,
                    new VkDescriptorImageInfo
                    {
                        imageView = texture.SampledView,
                        imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    }));
            }
        }

        uint? levelBinding = (kind, isUint) switch
        {
            (GpuTextureKind.Texture2D, true) => UintLevelBinding,
            (GpuTextureKind.Texture2D, false) => LevelBinding,
            (GpuTextureKind.Texture2DArray or GpuTextureKind.Cube, false) => ArrayLevelBinding,
            (GpuTextureKind.Texture3D, false) => VolumeLevelBinding,
            _ => null,
        };
        for (uint level = 0; level < texture.LevelViews.Length && level < LevelsPerTexture; level++)
        {
            if (levelBinding is not { } written) break;

            writes.Add((
                written,
                (id * LevelsPerTexture) + level,
                VkDescriptorType.StorageImage,
                new VkDescriptorImageInfo
                {
                    imageView = texture.LevelViews[level],
                    imageLayout = VkImageLayout.General
                }));
        }

        Write(writes);
    }

    /// <summary>
    /// Writes a sampler into the heap at <paramref name="id"/>, both as it samples and as it compares.
    /// </summary>
    internal void Write(uint id, VkSampler sampler)
    {
        if (id >= Samplers)
        {
            TellOverflow(ref _isSamplerOverflowTold, "samplers", Samplers);

            return;
        }

        VkDescriptorImageInfo image = new() { sampler = sampler };
        Write(
        [
            (SamplerBinding, id, VkDescriptorType.Sampler, image),
            (CompareSamplerBinding, id, VkDescriptorType.Sampler, image),
        ]);
    }

    private static void TellOverflow(ref bool isTold, string what, uint room)
    {
        if (isTold) return;

        isTold = true;
        Debugging.Error($"The GPU has made more {what} than its heap has room for ({room}): shaders cannot read those "
            + "past it.");
    }

    private void Write(List<(uint Binding, uint Element, VkDescriptorType Type, VkDescriptorImageInfo Image)> writes)
    {
        if (writes.Count == 0) return;

        VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[writes.Count];
        VkWriteDescriptorSet* sets = stackalloc VkWriteDescriptorSet[writes.Count];
        for (int i = 0; i < writes.Count; i++)
        {
            images[i] = writes[i].Image;
            sets[i] = new VkWriteDescriptorSet
            {
                dstSet = _set,
                dstBinding = writes[i].Binding,
                dstArrayElement = writes[i].Element,
                descriptorCount = 1,
                descriptorType = writes[i].Type,
                pImageInfo = &images[i],
            };
        }

        _device.Api.vkUpdateDescriptorSets((uint)writes.Count, sets, 0, null);
    }

    /// <summary>
    /// The heap's layout: each binding partially bound, as most of its slots hold nothing, and written while frames that
    /// read other slots of it are in flight.
    /// </summary>
    private VkDescriptorSetLayout CreateLayout((VkDescriptorType Type, uint Count)[] bindings)
    {
        VkDescriptorSetLayoutBinding* layoutBindings = stackalloc VkDescriptorSetLayoutBinding[BindingCount];
        VkDescriptorBindingFlags* flags = stackalloc VkDescriptorBindingFlags[BindingCount];
        for (int i = 0; i < BindingCount; i++)
        {
            layoutBindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)i,
                descriptorType = bindings[i].Type,
                descriptorCount = bindings[i].Count,
                stageFlags = VkShaderStageFlags.All,
            };
            flags[i] = VkDescriptorBindingFlags.PartiallyBound
                | VkDescriptorBindingFlags.UpdateAfterBind
                | VkDescriptorBindingFlags.UpdateUnusedWhilePending;
        }

        VkDescriptorSetLayoutBindingFlagsCreateInfo bindingFlags = new()
        {
            bindingCount = BindingCount,
            pBindingFlags = flags,
        };
        VkDescriptorSetLayoutCreateInfo info = new()
        {
            pNext = &bindingFlags,
            flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPool,
            bindingCount = BindingCount,
            pBindings = layoutBindings,
        };
        VkDescriptorSetLayout layout;
        VkResult result = _device.Api.vkCreateDescriptorSetLayout(&info, &layout);

        return result == VkResult.Success
            ? layout
            : throw new InvalidOperationException($"The GPU could not make the heap's layout: {result}.");
    }

    private VkDescriptorPool CreatePool((VkDescriptorType Type, uint Count)[] bindings)
    {
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[BindingCount];
        for (int i = 0; i < BindingCount; i++)
        {
            sizes[i] = new VkDescriptorPoolSize(bindings[i].Type, bindings[i].Count);
        }

        VkDescriptorPoolCreateInfo info = new()
        {
            flags = VkDescriptorPoolCreateFlags.UpdateAfterBind,
            maxSets = 1,
            poolSizeCount = BindingCount,
            pPoolSizes = sizes,
        };
        VkDescriptorPool pool;
        VkResult result = _device.Api.vkCreateDescriptorPool(&info, &pool);

        return result == VkResult.Success
            ? pool
            : throw new InvalidOperationException($"The GPU could not make the heap's pool: {result}.");
    }

    private VkDescriptorSet Allocate()
    {
        VkDescriptorSetLayout layout = _layout;
        VkDescriptorSetAllocateInfo info = new()
        {
            descriptorPool = _pool,
            descriptorSetCount = 1,
            pSetLayouts = &layout,
        };
        VkDescriptorSet set;
        VkResult result = _device.Api.vkAllocateDescriptorSets(&info, &set);

        return result == VkResult.Success
            ? set
            : throw new InvalidOperationException($"The GPU could not make the heap: {result}.");
    }

    private VkPipelineLayout CreatePipelineLayout()
    {
        VkDescriptorSetLayout layout = _layout;
        VkPushConstantRange constants = new()
        {
            stageFlags = VkShaderStageFlags.All,
            offset = 0,
            size = ConstantBytes,
        };
        VkPipelineLayoutCreateInfo info = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &layout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &constants,
        };
        VkPipelineLayout pipelineLayout;
        VkResult result = _device.Api.vkCreatePipelineLayout(&info, &pipelineLayout);

        return result == VkResult.Success
            ? pipelineLayout
            : throw new InvalidOperationException($"The GPU could not make the heap's pipeline layout: {result}.");
    }
}
