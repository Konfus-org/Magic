using Magic.Debugging;
using Magic.Gpu;
using Magic.Utils;
using Vortice.Vulkan;

namespace VulkanGraphics;

/// <summary>
/// A buffer as Vulkan holds it: its memory, mapped when the CPU may touch it.
/// </summary>
internal sealed unsafe class VulkanBuffer
{
    public required VkBuffer Buffer { get; init; }

    public required VkDeviceMemory Memory { get; init; }

    public required BufferDescription Description { get; init; }

    /// <summary>
    /// Where the CPU reads or writes it; null for device memory.
    /// </summary>
    public byte* Mapped { get; init; }
}

/// <summary>
/// A texture as Vulkan holds it: the image, a view of the whole for sampling and one per level for writing and
/// drawing into, and the layout each level is in now, which the transitions move.
/// </summary>
internal sealed class VulkanTexture
{
    public required VkImage Image { get; init; }

    /// <summary>
    /// Null for a window's image, whose memory is the swapchain's.
    /// </summary>
    public required VkDeviceMemory Memory { get; init; }

    public required TextureDescription Description { get; init; }

    public required VkImageView View { get; init; }

    public required VkImageView[] LevelViews { get; init; }

    public required VkImageLayout[] Layouts { get; init; }

    public bool IsWindow => Memory.IsNull;
}

internal sealed class VulkanPipeline
{
    public required VkPipeline Pipeline { get; init; }

    public required VkPipelineBindPoint BindPoint { get; init; }
}

/// <summary>
/// Every resource the heap holds, by its handle's id, and how each is made and freed. A freed resource goes to the
/// frame's trash and dies once the frames in flight are done with it; its id waits as long.
/// </summary>
internal sealed unsafe class VulkanResources(VulkanDevice device, VulkanHeap heap) : IDisposable
{
    private const float LodClampNone = 1000;

    private readonly VulkanBuffer?[] _buffers = new VulkanBuffer?[VulkanHeap.Buffers];
    private readonly VulkanTexture?[] _textures = new VulkanTexture?[VulkanHeap.Textures];
    private readonly VkSampler[] _samplers = new VkSampler[VulkanHeap.Samplers];
    private readonly List<VulkanPipeline?> _pipelines = [null];
    private readonly Stack<uint> _freeBuffers = new();
    private readonly Stack<uint> _freeTextures = new();
    private readonly Stack<uint> _freeSamplers = new();
    private readonly Stack<uint> _freePipelines = new();
    private uint _nextBuffer = 1;
    private uint _nextTexture = 1;
    private uint _nextSampler = 1;

    public void Dispose()
    {
        // Every resource still alive, whatever frame used it: the device is idle by now.
        for (uint id = 1; id < _nextBuffer; id++)
        {
            if (_buffers[id] is { } buffer)
                Free(buffer);
        }

        for (uint id = 1; id < _nextTexture; id++)
        {
            if (_textures[id] is { } texture)
                Free(texture);
        }

        for (uint id = 1; id < _nextSampler; id++)
        {
            if (_samplers[id].IsNotNull)
                device.Api.vkDestroySampler(_samplers[id]);
        }

        foreach (VulkanPipeline? pipeline in _pipelines)
        {
            if (pipeline is not null)
                device.Api.vkDestroyPipeline(pipeline.Pipeline);
        }
    }

    public VulkanBuffer? GetBuffer(uint id) => id < _buffers.Length ? _buffers[id] : null;

    public VulkanTexture? GetTexture(uint id) => id < _textures.Length ? _textures[id] : null;

    public VulkanPipeline? GetPipeline(uint id) => id < _pipelines.Count ? _pipelines[(int)id] : null;

    public Result<GpuBuffer> CreateBuffer(in BufferDescription description)
    {
        if (description.Bytes <= 0)
            return Result.Failure($"a buffer of {description.Bytes} bytes cannot be made.");

        if (!TryTakeId(_freeBuffers, ref _nextBuffer, VulkanHeap.Buffers, out uint id))
            return Result.Failure($"the heap holds its {VulkanHeap.Buffers} buffers already.");

        Result<VulkanBuffer> made = MakeBuffer(description);
        if (!made.Ok)
        {
            _freeBuffers.Push(id);

            return Result.Failure(made.Error);
        }

        _buffers[id] = made.Value;
        if (description.Usage.HasFlag(BufferUsages.Storage))
            heap.WriteBuffer(id, made.Value.Buffer, (ulong)description.Bytes);

        Name(VkObjectType.Buffer, made.Value.Buffer.Handle, description.Name);

        return Result.Success(new GpuBuffer(id));
    }

    public Result<GpuTexture> CreateTexture(in TextureDescription description)
    {
        if (description.Width <= 0 || description.Height <= 0 || description.Levels <= 0)
            return Result.Failure($"a texture of {description.Width} by {description.Height} cannot be made.");

        if (description.Levels > VulkanHeap.LevelsPerTexture)
            return Result.Failure($"a texture has at most {VulkanHeap.LevelsPerTexture} levels.");

        if (!TryTakeId(_freeTextures, ref _nextTexture, VulkanHeap.Textures, out uint id))
            return Result.Failure($"the heap holds its {VulkanHeap.Textures} textures already.");

        Result<VulkanTexture> made = MakeTexture(description);
        if (!made.Ok)
        {
            _freeTextures.Push(id);

            return Result.Failure(made.Error);
        }

        AddTexture(id, made.Value);
        Name(VkObjectType.Image, made.Value.Image.Handle, description.Name);

        return Result.Success(new GpuTexture(id));
    }

    /// <summary>
    /// A window's image as a texture of the heap: drawn into and copied to, never sampled.
    /// </summary>
    public Result<GpuTexture> AddWindowTexture(VkImage image, in TextureDescription description)
    {
        if (!TryTakeId(_freeTextures, ref _nextTexture, VulkanHeap.Textures, out uint id))
            return Result.Failure($"the heap holds its {VulkanHeap.Textures} textures already.");

        Result<VkImageView> view = MakeView(image, description, 0, description.Levels);
        if (!view.Ok)
        {
            _freeTextures.Push(id);

            return Result.Failure(view.Error);
        }

        VulkanTexture texture = new()
        {
            Image = image,
            Memory = VkDeviceMemory.Null,
            Description = description,
            View = view.Value,
            LevelViews = [view.Value],
            Layouts = [VkImageLayout.Undefined],
        };
        AddTexture(id, texture);

        return Result.Success(new GpuTexture(id));
    }

    /// <summary>
    /// Gives a buffer made with <see cref="MakeBuffer"/> a place in the heap, as the staging ring's are.
    /// </summary>
    public Result<GpuBuffer> Adopt(VulkanBuffer buffer)
    {
        if (!TryTakeId(_freeBuffers, ref _nextBuffer, VulkanHeap.Buffers, out uint id))
            return Result.Failure($"the heap holds its {VulkanHeap.Buffers} buffers already.");

        _buffers[id] = buffer;

        return Result.Success(new GpuBuffer(id));
    }

    public Result<GpuSampler> CreateSampler(in SamplerDescription description)
    {
        if (!TryTakeId(_freeSamplers, ref _nextSampler, VulkanHeap.Samplers, out uint id))
            return Result.Failure($"the heap holds its {VulkanHeap.Samplers} samplers already.");

        VkSamplerReductionModeCreateInfo reduction = new() { reductionMode = description.Reduction.ToVulkan() };
        bool hasReduction = description.Reduction != SamplerReduction.Average && device.Features.HasSamplerMinMax;
        VkFilter filter = description.Filter.ToVulkan();
        VkSamplerAddressMode wrap = description.Wrap.ToVulkan();
        VkSamplerCreateInfo createInfo = new()
        {
            pNext = hasReduction ? &reduction : null,
            magFilter = filter,
            minFilter = filter,
            mipmapMode = description.HasMipmapBlend ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest,
            addressModeU = wrap,
            addressModeV = wrap,
            addressModeW = wrap,
            anisotropyEnable = description.Anisotropy > 1,
            maxAnisotropy = Math.Clamp(description.Anisotropy, 1, 16),
            minLod = 0,
            maxLod = LodClampNone,
        };
        VkResult result = device.Api.vkCreateSampler(&createInfo, out VkSampler sampler);
        if (result != VkResult.Success)
        {
            _freeSamplers.Push(id);

            return Result.Failure($"Vulkan could not make a sampler: {result}.");
        }

        _samplers[id] = sampler;
        heap.WriteSampler(id, sampler);

        return Result.Success(new GpuSampler(id));
    }

    public Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description)
    {
        Result<VkShaderModule> module = MakeModule(description.Spirv);
        if (!module.Ok)
            return Result.Failure(module.Error);

        using NativeStrings entry = new([description.EntryPoint]);
        VkComputePipelineCreateInfo createInfo = new()
        {
            stage = new VkPipelineShaderStageCreateInfo
            {
                stage = VkShaderStageFlags.Compute,
                module = module.Value,
                pName = entry.Pointers[0],
            },
            layout = heap.PipelineLayout,
        };
        VkPipeline pipeline;
        VkResult result = device.Api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &createInfo, &pipeline);
        device.Api.vkDestroyShaderModule(module.Value);
        if (result != VkResult.Success)
            return Result.Failure($"Vulkan could not build the compute pipeline {description.Name}: {result}.");

        Name(VkObjectType.Pipeline, pipeline.Handle, description.Name);

        return Result.Success(AddPipeline(pipeline, VkPipelineBindPoint.Compute));
    }

    public Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description)
    {
        Result<VkShaderModule> vertex = MakeModule(description.VertexSpirv);
        if (!vertex.Ok)
            return Result.Failure(vertex.Error);

        Result<VkShaderModule> pixel = MakeModule(description.PixelSpirv);
        if (!pixel.Ok)
        {
            device.Api.vkDestroyShaderModule(vertex.Value);

            return Result.Failure(pixel.Error);
        }

        using NativeStrings entries = new([description.VertexEntryPoint, description.PixelEntryPoint]);
        VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Vertex,
            module = vertex.Value,
            pName = entries.Pointers[0],
        };
        stages[1] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Fragment,
            module = pixel.Value,
            pName = entries.Pointers[1],
        };

        int colorCount = description.ColorFormats.Length;
        VkFormat* colorFormats = stackalloc VkFormat[Math.Max(1, colorCount)];
        VkPipelineColorBlendAttachmentState* blends =
            stackalloc VkPipelineColorBlendAttachmentState[Math.Max(1, colorCount)];
        for (int i = 0; i < colorCount; i++)
        {
            colorFormats[i] = description.ColorFormats[i].ToVulkan();
            blends[i] = description.Blend.ToBlendState();
        }

        VkPipelineRenderingCreateInfo rendering = new()
        {
            colorAttachmentCount = (uint)colorCount,
            pColorAttachmentFormats = colorFormats,
            depthAttachmentFormat = description.DepthFormat.ToVulkan(),
        };
        VkPipelineVertexInputStateCreateInfo vertexInput = new();
        VkPipelineInputAssemblyStateCreateInfo assembly = new(VkPrimitiveTopology.TriangleList, false);
        VkPipelineViewportStateCreateInfo viewport = new(1, 1);
        VkPipelineRasterizationStateCreateInfo rasterization = new(
            description.Cull.ToVulkan(),
            VkPolygonMode.Fill,
            VkFrontFace.Clockwise
        );
        VkPipelineMultisampleStateCreateInfo multisample = new(VkSampleCountFlags.Count1);
        // Depth is reversed, so nearer is greater.
        VkPipelineDepthStencilStateCreateInfo depth = new(
            description.Depth != DepthMode.None,
            description.Depth == DepthMode.TestAndWrite,
            VkCompareOp.GreaterOrEqual
        );
        VkPipelineColorBlendStateCreateInfo blend = new((uint)colorCount, blends);
        VkDynamicState* dynamicStates = stackalloc VkDynamicState[2]
        {
            VkDynamicState.Viewport,
            VkDynamicState.Scissor,
        };
        VkPipelineDynamicStateCreateInfo dynamic = new() { dynamicStateCount = 2, pDynamicStates = dynamicStates };
        VkGraphicsPipelineCreateInfo createInfo = new()
        {
            pNext = &rendering,
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInput,
            pInputAssemblyState = &assembly,
            pViewportState = &viewport,
            pRasterizationState = &rasterization,
            pMultisampleState = &multisample,
            pDepthStencilState = &depth,
            pColorBlendState = &blend,
            pDynamicState = &dynamic,
            layout = heap.PipelineLayout,
        };
        VkPipeline pipeline;
        VkResult result = device.Api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &createInfo, &pipeline);
        device.Api.vkDestroyShaderModule(vertex.Value);
        device.Api.vkDestroyShaderModule(pixel.Value);
        if (result != VkResult.Success)
            return Result.Failure($"Vulkan could not build the graphics pipeline {description.Name}: {result}.");

        Name(VkObjectType.Pipeline, pipeline.Handle, description.Name);

        return Result.Success(AddPipeline(pipeline, VkPipelineBindPoint.Graphics));
    }

    /// <summary>
    /// Takes a buffer out of the heap; what it was is handed back to die with the frame.
    /// </summary>
    public VulkanBuffer? RemoveBuffer(uint id)
    {
        VulkanBuffer? buffer = GetBuffer(id);
        if (buffer is not null)
            _buffers[id] = null;

        return buffer;
    }

    public VulkanTexture? RemoveTexture(uint id)
    {
        VulkanTexture? texture = GetTexture(id);
        if (texture is not null)
            _textures[id] = null;

        return texture;
    }

    public VkSampler RemoveSampler(uint id)
    {
        if (id >= _samplers.Length)
            return VkSampler.Null;

        VkSampler sampler = _samplers[id];
        _samplers[id] = VkSampler.Null;

        return sampler;
    }

    public VulkanPipeline? RemovePipeline(uint id)
    {
        VulkanPipeline? pipeline = GetPipeline(id);
        if (pipeline is not null)
            _pipelines[(int)id] = null;

        return pipeline;
    }

    /// <summary>
    /// Frees what <see cref="RemoveBuffer"/> handed back, once no frame uses it, and lets its id be taken again.
    /// </summary>
    public void Free(uint id, VulkanBuffer buffer)
    {
        Free(buffer);
        _freeBuffers.Push(id);
    }

    public void Free(uint id, VulkanTexture texture)
    {
        Free(texture);
        _freeTextures.Push(id);
    }

    public void Free(uint id, VkSampler sampler)
    {
        device.Api.vkDestroySampler(sampler);
        _freeSamplers.Push(id);
    }

    public void Free(uint id, VulkanPipeline pipeline)
    {
        device.Api.vkDestroyPipeline(pipeline.Pipeline);
        _freePipelines.Push(id);
    }

    /// <summary>
    /// Allocates memory for <paramref name="requirements"/> of a type with <paramref name="wanted"/>, else with
    /// <paramref name="fallback"/>.
    /// </summary>
    public Result<VkDeviceMemory> Allocate(
        in VkMemoryRequirements requirements,
        VkMemoryPropertyFlags wanted,
        VkMemoryPropertyFlags fallback,
        bool hasDeviceAddress
    )
    {
        int type = device.FindMemoryType(requirements.memoryTypeBits, wanted);
        if (type < 0)
            type = device.FindMemoryType(requirements.memoryTypeBits, fallback);

        if (type < 0)
            return Result.Failure("no memory type suits it.");

        VkMemoryAllocateFlagsInfo flags = new() { flags = VkMemoryAllocateFlags.DeviceAddress };
        VkMemoryAllocateInfo allocateInfo = new()
        {
            pNext = hasDeviceAddress ? &flags : null,
            allocationSize = requirements.size,
            memoryTypeIndex = (uint)type,
        };
        VkResult result = device.Api.vkAllocateMemory(&allocateInfo, out VkDeviceMemory memory);

        return result == VkResult.Success
            ? Result.Success(memory)
            : Result.Failure($"Vulkan gave no memory for {requirements.size} bytes: {result}.");
    }

    public Result<VulkanBuffer> MakeBuffer(in BufferDescription description)
    {
        VkBufferCreateInfo createInfo = new()
        {
            size = (ulong)description.Bytes,
            usage = description.Usage.ToVulkan(),
            sharingMode = VkSharingMode.Exclusive,
        };
        VkResult result = device.Api.vkCreateBuffer(&createInfo, out VkBuffer buffer);
        if (result != VkResult.Success)
            return Result.Failure($"Vulkan could not make a buffer of {description.Bytes} bytes: {result}.");

        bool isHost =
            description.Usage.HasFlag(BufferUsages.Upload) || description.Usage.HasFlag(BufferUsages.Readback);
        VkMemoryPropertyFlags wanted =
            description.Usage.HasFlag(BufferUsages.Readback)
                ? VkMemoryPropertyFlags.HostVisible
                    | VkMemoryPropertyFlags.HostCoherent
                    | VkMemoryPropertyFlags.HostCached
            : isHost ? VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent
            : VkMemoryPropertyFlags.DeviceLocal;
        VkMemoryPropertyFlags fallback = isHost
            ? VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent
            : VkMemoryPropertyFlags.None;
        device.Api.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        Result<VkDeviceMemory> memory = Allocate(
            requirements,
            wanted,
            fallback,
            description.Usage.HasFlag(BufferUsages.Storage)
        );
        if (!memory.Ok)
        {
            device.Api.vkDestroyBuffer(buffer);

            return Result.Failure($"a buffer of {description.Bytes} bytes was not made: {memory.Error}");
        }

        device.Api.vkBindBufferMemory(buffer, memory.Value, 0);
        byte* mapped = null;
        if (isHost)
        {
            void* pointer;
            result = device.Api.vkMapMemory(memory.Value, 0, (ulong)description.Bytes, VkMemoryMapFlags.None, &pointer);
            if (result != VkResult.Success)
            {
                device.Api.vkDestroyBuffer(buffer);
                device.Api.vkFreeMemory(memory.Value);

                return Result.Failure($"Vulkan could not map a buffer of {description.Bytes} bytes: {result}.");
            }

            mapped = (byte*)pointer;
        }

        return Result.Success(
            new VulkanBuffer
            {
                Buffer = buffer,
                Memory = memory.Value,
                Description = description,
                Mapped = mapped,
            }
        );
    }

    private static VkImageAspectFlags GetAspect(in TextureDescription description) =>
        description.Format.IsDepth ? VkImageAspectFlags.Depth : VkImageAspectFlags.Color;

    private static bool TryTakeId(Stack<uint> free, ref uint next, uint capacity, out uint id)
    {
        if (free.TryPop(out id))
            return true;

        if (next >= capacity)
            return false;

        id = next++;

        return true;
    }

    private void Free(VulkanBuffer buffer)
    {
        device.Api.vkDestroyBuffer(buffer.Buffer);
        device.Api.vkFreeMemory(buffer.Memory);
    }

    private void Free(VulkanTexture texture)
    {
        foreach (VkImageView view in texture.LevelViews)
        {
            if (view != texture.View)
                device.Api.vkDestroyImageView(view);
        }

        device.Api.vkDestroyImageView(texture.View);
        if (texture.IsWindow)
            return;

        device.Api.vkDestroyImage(texture.Image);
        device.Api.vkFreeMemory(texture.Memory);
    }

    private void AddTexture(uint id, VulkanTexture texture)
    {
        _textures[id] = texture;
        if (!texture.IsWindow)
            heap.WriteTexture(id, texture.Description, texture.View, texture.LevelViews);
    }

    private GpuPipeline AddPipeline(VkPipeline pipeline, VkPipelineBindPoint bindPoint)
    {
        VulkanPipeline made = new() { Pipeline = pipeline, BindPoint = bindPoint };
        if (_freePipelines.TryPop(out uint id))
        {
            _pipelines[(int)id] = made;

            return new GpuPipeline(id);
        }

        _pipelines.Add(made);

        return new GpuPipeline((uint)_pipelines.Count - 1);
    }

    private Result<VulkanTexture> MakeTexture(in TextureDescription description)
    {
        bool isCube = description.Kind == TextureKind.Cube;
        VkImageCreateInfo createInfo = new()
        {
            flags = isCube ? VkImageCreateFlags.CubeCompatible : VkImageCreateFlags.None,
            imageType = description.Kind.ToImageType(),
            format = description.Format.ToVulkan(),
            extent = new VkExtent3D(
                (uint)description.Width,
                (uint)description.Height,
                (uint)(description.Kind == TextureKind.Texture3D ? description.Depth : 1)
            ),
            mipLevels = (uint)description.Levels,
            arrayLayers = (uint)(
                isCube ? 6
                : description.Kind == TextureKind.Texture2DArray ? description.Layers
                : 1
            ),
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = description.Usage.ToVulkan(),
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        VkResult result = device.Api.vkCreateImage(&createInfo, out VkImage image);
        if (result != VkResult.Success)
            return Result.Failure($"Vulkan could not make the texture {description.Name}: {result}.");

        device.Api.vkGetImageMemoryRequirements(image, out VkMemoryRequirements requirements);
        Result<VkDeviceMemory> memory = Allocate(
            requirements,
            VkMemoryPropertyFlags.DeviceLocal,
            VkMemoryPropertyFlags.None,
            hasDeviceAddress: false
        );
        if (!memory.Ok)
        {
            device.Api.vkDestroyImage(image);

            return Result.Failure($"the texture {description.Name} was not made: {memory.Error}");
        }

        device.Api.vkBindImageMemory(image, memory.Value, 0);
        Result<VkImageView> view = MakeView(image, description, 0, description.Levels);
        if (!view.Ok)
        {
            device.Api.vkDestroyImage(image);
            device.Api.vkFreeMemory(memory.Value);

            return Result.Failure(view.Error);
        }

        VkImageView[] levelViews = new VkImageView[description.Levels];
        for (int level = 0; level < description.Levels; level++)
        {
            Result<VkImageView> levelView = MakeView(image, description, level, 1);
            if (!levelView.Ok)
            {
                for (int made = 0; made < level; made++)
                {
                    device.Api.vkDestroyImageView(levelViews[made]);
                }

                device.Api.vkDestroyImageView(view.Value);
                device.Api.vkDestroyImage(image);
                device.Api.vkFreeMemory(memory.Value);

                return Result.Failure(levelView.Error);
            }

            levelViews[level] = levelView.Value;
        }

        VkImageLayout[] layouts = new VkImageLayout[description.Levels];
        Array.Fill(layouts, VkImageLayout.Undefined);

        return Result.Success(
            new VulkanTexture
            {
                Image = image,
                Memory = memory.Value,
                Description = description,
                View = view.Value,
                LevelViews = levelViews,
                Layouts = layouts,
            }
        );
    }

    /// <summary>
    /// A view of <paramref name="levels"/> levels from <paramref name="firstLevel"/>: the whole texture as its kind
    /// is sampled, or one level as it is written and drawn into, where a cube is six layers.
    /// </summary>
    private Result<VkImageView> MakeView(VkImage image, in TextureDescription description, int firstLevel, int levels)
    {
        bool isWhole = levels == description.Levels && firstLevel == 0;
        bool isCube = description.Kind == TextureKind.Cube;
        VkImageViewType viewType = isWhole || !isCube ? description.Kind.ToViewType() : VkImageViewType.Image2DArray;
        uint layers = (uint)(
            isCube ? 6
            : description.Kind == TextureKind.Texture2DArray ? description.Layers
            : 1
        );
        VkImageViewCreateInfo createInfo = new()
        {
            image = image,
            viewType = viewType,
            format = description.Format.ToVulkan(),
            components = VkComponentMapping.Identity,
            subresourceRange = new VkImageSubresourceRange(
                GetAspect(description),
                (uint)firstLevel,
                (uint)levels,
                0,
                layers
            ),
        };
        VkResult result = device.Api.vkCreateImageView(&createInfo, out VkImageView view);

        return result == VkResult.Success
            ? Result.Success(view)
            : Result.Failure($"Vulkan could not view the texture {description.Name}: {result}.");
    }

    private Result<VkShaderModule> MakeModule(ReadOnlyMemory<byte> spirv)
    {
        if (spirv.Length == 0 || spirv.Length % 4 != 0)
            return Result.Failure($"{spirv.Length} bytes are not SPIR-V.");

        fixed (byte* code = spirv.Span)
        {
            VkShaderModuleCreateInfo createInfo = new() { codeSize = (nuint)spirv.Length, pCode = (uint*)code };
            VkResult result = device.Api.vkCreateShaderModule(&createInfo, out VkShaderModule module);

            return result == VkResult.Success
                ? Result.Success(module)
                : Result.Failure($"Vulkan refused the shader's SPIR-V: {result}.");
        }
    }

    /// <summary>
    /// What a GPU debugger shows the object as, when the debug extension is there and the name is not empty.
    /// </summary>
    private void Name(VkObjectType type, ulong handle, string name)
    {
        if (!device.HasDebugUtils || name.Length == 0)
            return;

        VkResult result = device.InstanceApi.vkSetDebugUtilsObjectNameEXT(device.Device, type, handle, name);
        if (result != VkResult.Success)
            Log.Verbose($"{name} could not be named for the debugger: {result}.");
    }
}
