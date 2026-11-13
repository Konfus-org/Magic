using System.Linq;
using Magic.Assets;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

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

    /// <summary>
    /// Its device address, for work that reads it by address; 0 when none.
    /// </summary>
    public ulong Address { get; init; }

    /// <summary>
    /// What the last pass that used it used it as, which the next one's barrier moves from.
    /// </summary>
    public GpuAccess Access { get; set; }
}

/// <summary>
/// An acceleration structure as Vulkan holds it: the structure in its buffer, the scratch its builds use, what it is
/// built over, and its address.
/// </summary>
internal sealed unsafe class VulkanAcceleration
{
    public required VkAccelerationStructureKHR Handle { get; init; }

    public required VulkanBuffer Buffer { get; init; }

    /// <summary>
    /// None for a compacted copy, which is built already and is not built again.
    /// </summary>
    public required VulkanBuffer? Scratch { get; init; }

    public required VkAccelerationStructureTypeKHR Type { get; init; }

    /// <summary>
    /// The name it was asked for under, which its compacted copy takes.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The geometries built over: a bottom-level structure's triangles, or a top-level one's instances.
    /// </summary>
    public required VkAccelerationStructureGeometryKHR[] Geometries { get; init; }

    public required uint[] PrimitiveCounts { get; init; }

    public required ulong Address { get; init; }

    /// <summary>
    /// Gets the build flags for a structure of <paramref name="type"/>: fast tracing, and compaction for a bottom-level
    /// structure.
    /// </summary>
    public static VkBuildAccelerationStructureFlagsKHR GetBuildFlags(VkAccelerationStructureTypeKHR type) =>
        type == VkAccelerationStructureTypeKHR.BottomLevel
            ? VkBuildAccelerationStructureFlagsKHR.PreferFastTrace
                | VkBuildAccelerationStructureFlagsKHR.AllowCompaction
            : VkBuildAccelerationStructureFlagsKHR.PreferFastTrace;
}

/// <summary>
/// A texture as Vulkan holds it: the image, its views, and the access that each level was last used as.
/// </summary>
/// <remarks>
/// It has a view of the whole texture for sampling, and a view of each level for writing and drawing into. The layout
/// of each level follows its access, and the barriers change it.
/// </remarks>
internal sealed class VulkanTexture
{
    public required VkImage Image { get; init; }

    /// <summary>
    /// Null for a window's image, whose memory is the swapchain's.
    /// </summary>
    public required VkDeviceMemory Memory { get; init; }

    public required TextureDescription Description { get; init; }

    /// <summary>
    /// Null for a texture only copied, which has no views.
    /// </summary>
    public VkImageView View { get; set; }

    public required VkImageView[] LevelViews { get; init; }

    public required GpuAccess[] Accesses { get; init; }

    public bool IsWindow => Memory.IsNull;
}

internal sealed class VulkanPipeline
{
    public required VkPipeline Pipeline { get; init; }

    public required VkPipelineBindPoint BindPoint { get; init; }
}

/// <summary>
/// Holds resources of one kind by the ids of their handles, at most <paramref name="capacity"/>; id 0 is never taken.
/// </summary>
/// <remarks>
/// The id of a removed resource stays taken until it is released, because the frames in flight can still name it.
/// </remarks>
internal sealed class Slots<T>(uint capacity)
{
    private readonly List<T?> _items = [default];
    private readonly Stack<uint> _free = new();

    /// <summary>
    /// Every resource held.
    /// </summary>
    public IEnumerable<T> Items => _items.OfType<T>();

    public T? this[uint id] => id < _items.Count ? _items[(int)id] : default;

    public bool TryAdd(T item, out uint id)
    {
        if (_free.TryPop(out id))
        {
            _items[(int)id] = item;

            return true;
        }

        if (_items.Count >= capacity)
            return false;

        id = (uint)_items.Count;
        _items.Add(item);

        return true;
    }

    /// <summary>
    /// Takes the resource out and hands it back, its id still taken; null when there is none.
    /// </summary>
    public T? Remove(uint id)
    {
        T? item = this[id];
        if (item is not null)
            _items[(int)id] = default;

        return item;
    }

    /// <summary>
    /// Lets a removed resource's id be taken again.
    /// </summary>
    public void Release(uint id) => _free.Push(id);
}

/// <summary>
/// Makes, holds and frees every resource of the heap, by the id of its handle.
/// </summary>
/// <remarks>
/// A freed resource goes to the frame's trash, and is destroyed after the frames in flight are done with it. Its id
/// waits as long.
/// </remarks>
internal sealed unsafe class VulkanResources(VulkanDevice device, VulkanHeap heap) : IDisposable
{
    private const float LodClampNone = 1000;

    /// <summary>
    /// Every allocation's size, so what is freed takes off what it added.
    /// </summary>
    private readonly Dictionary<VkDeviceMemory, ulong> _allocations = [];

    public Slots<VulkanBuffer> Buffers { get; } = new(VulkanHeap.Buffers);

    public Slots<VulkanTexture> Textures { get; } = new(VulkanHeap.Textures);

    public Slots<VkSampler?> Samplers { get; } = new(VulkanHeap.Samplers);

    public Slots<VulkanPipeline> Pipelines { get; } = new(uint.MaxValue);

    public Slots<VulkanAcceleration> Accelerations { get; } = new(VulkanHeap.Accelerations);

    /// <summary>
    /// Bytes of device memory the resources hold now.
    /// </summary>
    public long MemoryBytes { get; private set; }

    public void Dispose()
    {
        // Every resource still alive, whatever frame used it: the device is idle by now.
        foreach (VulkanBuffer buffer in Buffers.Items)
        {
            Free(buffer);
        }

        foreach (VulkanTexture texture in Textures.Items)
        {
            Free(texture);
        }

        foreach (VkSampler? sampler in Samplers.Items)
        {
            device.Api.vkDestroySampler(sampler ?? VkSampler.Null);
        }

        foreach (VulkanAcceleration acceleration in Accelerations.Items)
        {
            Free(acceleration);
        }

        foreach (VulkanPipeline pipeline in Pipelines.Items)
        {
            device.Api.vkDestroyPipeline(pipeline.Pipeline);
        }
    }

    /// <summary>
    /// Creates a bottom-level acceleration structure over triangles in the heap's buffers, and puts it in the heap for
    /// the recorder to build.
    /// </summary>
    public Result<GpuAcceleration> CreateAcceleration(in BottomLevelDescription description)
    {
        if (description.Geometries.IsDefaultOrEmpty)
            return Result.Failure("an acceleration structure over no geometry cannot be made.");

        VkAccelerationStructureGeometryKHR[] geometries = new VkAccelerationStructureGeometryKHR[
            description.Geometries.Length
        ];
        uint[] counts = new uint[description.Geometries.Length];
        for (int i = 0; i < geometries.Length; i++)
        {
            TriangleGeometry triangles = description.Geometries[i];
            if (Buffers[triangles.Vertices.Id] is not { } vertices || Buffers[triangles.Indices.Id] is not { } indices)
                return Result.Failure("an acceleration structure's buffers are gone.");

            geometries[i] = new VkAccelerationStructureGeometryKHR
            {
                geometryType = VkGeometryTypeKHR.Triangles,
                flags = VkGeometryFlagsKHR.Opaque | VkGeometryFlagsKHR.NoDuplicateAnyHitInvocation,
                geometry = new VkAccelerationStructureGeometryDataKHR
                {
                    triangles = new VkAccelerationStructureGeometryTrianglesDataKHR
                    {
                        vertexFormat = VkFormat.R32G32B32Sfloat,
                        vertexData = new VkDeviceOrHostAddressConstKHR
                        {
                            deviceAddress = vertices.Address + triangles.VertexOffset,
                        },
                        vertexStride = triangles.VertexStride,
                        maxVertex = triangles.VertexCount - 1,
                        indexType = VkIndexType.Uint32,
                        indexData = new VkDeviceOrHostAddressConstKHR
                        {
                            deviceAddress = indices.Address + triangles.IndexOffset,
                        },
                    },
                },
            };
            counts[i] = triangles.TriangleCount;
        }

        return CreateAcceleration(VkAccelerationStructureTypeKHR.BottomLevel, geometries, counts, description.Name);
    }

    /// <summary>
    /// Creates a top-level acceleration structure over the instances in a buffer, and puts it in the heap for the
    /// recorder to build.
    /// </summary>
    /// <remarks>
    /// It is sized for the most instances that it can hold.
    /// </remarks>
    public Result<GpuAcceleration> CreateAcceleration(in TopLevelDescription description)
    {
        if (description.MaxInstances <= 0)
            return Result.Failure("an acceleration structure over no instances cannot be made.");

        if (Buffers[description.Instances.Id] is not { } instances)
            return Result.Failure("an acceleration structure's instance buffer is gone.");

        VkAccelerationStructureGeometryKHR[] geometries =
        [
            new()
            {
                geometryType = VkGeometryTypeKHR.Instances,
                geometry = new VkAccelerationStructureGeometryDataKHR
                {
                    instances = new VkAccelerationStructureGeometryInstancesDataKHR
                    {
                        arrayOfPointers = false,
                        data = new VkDeviceOrHostAddressConstKHR { deviceAddress = instances.Address },
                    },
                },
            },
        ];

        return CreateAcceleration(
            VkAccelerationStructureTypeKHR.TopLevel,
            geometries,
            [(uint)description.MaxInstances],
            description.Name
        );
    }

    public Result<GpuBuffer> CreateBuffer(in BufferDescription description)
    {
        if (description.Bytes <= 0)
            return Result.Failure($"a buffer of {description.Bytes} bytes cannot be made.");

        Result<VulkanBuffer> made = CreateVulkanBuffer(description);
        if (!made.Ok)
            return Result.Failure(made.Error);

        if (!Buffers.TryAdd(made.Value, out uint id))
        {
            Free(made.Value);

            return Result.Failure($"the heap holds its {VulkanHeap.Buffers} buffers already.");
        }

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

        Result<VulkanTexture> made = CreateVulkanTexture(description);
        if (!made.Ok)
            return Result.Failure(made.Error);

        if (!Textures.TryAdd(made.Value, out uint id))
        {
            Free(made.Value);

            return Result.Failure($"the heap holds its {VulkanHeap.Textures} textures already.");
        }

        heap.WriteTexture(id, description, made.Value.View, made.Value.LevelViews);
        Name(VkObjectType.Image, made.Value.Image.Handle, description.Name);

        return Result.Success(new GpuTexture(id));
    }

    /// <summary>
    /// Adds a window's image as a texture of the heap, which is drawn into and copied to, but never sampled.
    /// </summary>
    public Result<GpuTexture> AddWindowTexture(VkImage image, in TextureDescription description)
    {
        Result<VkImageView> view = CreateView(image, description, 0, description.Levels);
        if (!view.Ok)
            return Result.Failure(view.Error);

        VulkanTexture texture = new()
        {
            Image = image,
            Memory = VkDeviceMemory.Null,
            Description = description,
            View = view.Value,
            LevelViews = [view.Value],
            Accesses = [GpuAccess.None],
        };
        if (!Textures.TryAdd(texture, out uint id))
        {
            Free(texture);

            return Result.Failure($"the heap holds its {VulkanHeap.Textures} textures already.");
        }

        return Result.Success(new GpuTexture(id));
    }

    public Result<GpuSampler> CreateSampler(in SamplerDescription description)
    {
        VkFilter filter = description.Filter.ToVulkan();
        VkSamplerAddressMode wrap = description.Wrap.ToVulkan();
        VkSamplerCreateInfo createInfo = new()
        {
            magFilter = filter,
            minFilter = filter,
            mipmapMode =
                description.MipFilter == TextureFilter.Linear
                    ? VkSamplerMipmapMode.Linear
                    : VkSamplerMipmapMode.Nearest,
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
            return Result.Failure($"Vulkan could not make a sampler: {result}.");

        if (!Samplers.TryAdd(sampler, out uint id))
        {
            device.Api.vkDestroySampler(sampler);

            return Result.Failure($"the heap holds its {VulkanHeap.Samplers} samplers already.");
        }

        heap.WriteSampler(id, sampler);

        return Result.Success(new GpuSampler(id));
    }

    public Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description)
    {
        Result<VkShaderModule> module = CreateModule(description.Spirv);
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

        return AddPipeline(pipeline, VkPipelineBindPoint.Compute);
    }

    public Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description)
    {
        Result<VkShaderModule> vertex = CreateModule(description.VertexSpirv);
        if (!vertex.Ok)
            return Result.Failure(vertex.Error);

        Result<VkShaderModule> pixel = CreateModule(description.PixelSpirv);
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

        return AddPipeline(pipeline, VkPipelineBindPoint.Graphics);
    }

    /// <summary>
    /// Frees what a frame threw away after no frame uses it, and lets its id be taken again.
    /// </summary>
    /// <remarks>
    /// A buffer can still be in the heap under that id, such as a staging ring that grew. It leaves the heap now.
    /// </remarks>
    public void Free(VulkanFrames.Trash trash)
    {
        switch (trash.Resource)
        {
            case VulkanBuffer buffer:
                if (ReferenceEquals(Buffers[trash.Id], buffer))
                    Buffers.Remove(trash.Id);

                Free(buffer);
                Buffers.Release(trash.Id);
                break;
            case VulkanTexture texture:
                Free(texture);
                Textures.Release(trash.Id);
                break;
            case VkSampler sampler:
                device.Api.vkDestroySampler(sampler);
                Samplers.Release(trash.Id);
                break;
            case VulkanPipeline pipeline:
                device.Api.vkDestroyPipeline(pipeline.Pipeline);
                Pipelines.Release(trash.Id);
                break;
            case VulkanAcceleration acceleration:
                Free(acceleration);
                Accelerations.Release(trash.Id);
                break;
            default:
                throw new ArgumentException($"{trash.Resource} is not a resource of the heap.", nameof(trash));
        }
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
        if (result != VkResult.Success)
            return Result.Failure($"Vulkan gave no memory for {requirements.size} bytes: {result}.");

        _allocations[memory] = requirements.size;
        MemoryBytes += (long)requirements.size;

        return Result.Success(memory);
    }

    /// <summary>
    /// Records into <paramref name="cmd"/> a copy of a built bottom-level structure at its compacted size
    /// <paramref name="bytes"/>, and puts the copy in the heap with no scratch.
    /// </summary>
    /// <remarks>
    /// The build's barrier, in the frame that built it, made the build visible to the copy. The next build that reads
    /// the copy waits for every command before it.
    /// </remarks>
    public Result<GpuAcceleration> Compact(VkCommandBuffer cmd, VulkanAcceleration built, ulong bytes)
    {
        Result<VulkanBuffer> buffer = CreateVulkanBuffer(
            new BufferDescription(built.Name, (long)bytes, BufferUsages.Acceleration)
        );
        if (!buffer.Ok)
            return Result.Failure($"{built.Name} was not compacted: {buffer.Error}");

        Result<GpuAcceleration> compacted = AddAcceleration(
            built.Type,
            buffer.Value,
            null,
            built.Geometries,
            built.PrimitiveCounts,
            built.Name
        );
        if (!compacted.Ok || Accelerations[compacted.Value.Id] is not { } into)
            return compacted;

        VkCopyAccelerationStructureInfoKHR copy = new()
        {
            src = built.Handle,
            dst = into.Handle,
            mode = VkCopyAccelerationStructureModeKHR.Compact,
        };
        device.Api.vkCmdCopyAccelerationStructureKHR(cmd, &copy);

        return compacted;
    }

    private Result<VulkanBuffer> CreateVulkanBuffer(in BufferDescription description)
    {
        VkBufferCreateInfo createInfo = new()
        {
            size = (ulong)description.Bytes,
            usage = description.Usage.ToVulkan(device.Features.HasRayQuery),
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
            description.Usage.HasFlag(BufferUsages.Storage) || description.Usage.HasFlag(BufferUsages.Acceleration)
        );
        if (!memory.Ok)
        {
            device.Api.vkDestroyBuffer(buffer);

            return Result.Failure($"a buffer of {description.Bytes} bytes was not made: {memory.Error}");
        }

        device.Api.vkBindBufferMemory(buffer, memory.Value, 0);
        ulong address = 0;
        if (description.Usage.HasFlag(BufferUsages.Storage) || description.Usage.HasFlag(BufferUsages.Acceleration))
        {
            VkBufferDeviceAddressInfo addressInfo = new() { buffer = buffer };
            address = device.Api.vkGetBufferDeviceAddress(&addressInfo);
        }

        byte* mapped = null;
        if (isHost)
        {
            void* pointer;
            result = device.Api.vkMapMemory(memory.Value, 0, (ulong)description.Bytes, VkMemoryMapFlags.None, &pointer);
            if (result != VkResult.Success)
            {
                device.Api.vkDestroyBuffer(buffer);
                FreeMemory(memory.Value);

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
                Address = address,
            }
        );
    }

    private void Free(VulkanBuffer buffer)
    {
        device.Api.vkDestroyBuffer(buffer.Buffer);
        FreeMemory(buffer.Memory);
    }

    private void FreeMemory(VkDeviceMemory memory)
    {
        device.Api.vkFreeMemory(memory);
        if (_allocations.Remove(memory, out ulong bytes))
            MemoryBytes -= (long)bytes;
    }

    /// <summary>
    /// Creates an acceleration structure of a type over geometries, at the driver's size, with its buffer, its scratch
    /// and its place in the heap.
    /// </summary>
    private Result<GpuAcceleration> CreateAcceleration(
        VkAccelerationStructureTypeKHR type,
        VkAccelerationStructureGeometryKHR[] geometries,
        uint[] counts,
        string name
    )
    {
        if (!device.Features.HasRayQuery)
            return Result.Failure("the GPU traces no rays.");

        VkAccelerationStructureBuildSizesInfoKHR sizes;
        fixed (VkAccelerationStructureGeometryKHR* geometriesPointer = geometries)
        fixed (uint* countsPointer = counts)
        {
            VkAccelerationStructureBuildGeometryInfoKHR buildInfo = new()
            {
                type = type,
                flags = VulkanAcceleration.GetBuildFlags(type),
                mode = VkBuildAccelerationStructureModeKHR.Build,
                geometryCount = (uint)geometries.Length,
                pGeometries = geometriesPointer,
            };
            sizes = new VkAccelerationStructureBuildSizesInfoKHR();
            device.Api.vkGetAccelerationStructureBuildSizesKHR(
                VkAccelerationStructureBuildTypeKHR.Device,
                &buildInfo,
                countsPointer,
                &sizes
            );
        }

        Result<VulkanBuffer> buffer = CreateVulkanBuffer(
            new BufferDescription(name, (long)sizes.accelerationStructureSize, BufferUsages.Acceleration)
        );
        Result<VulkanBuffer> scratch = CreateVulkanBuffer(
            new BufferDescription($"{name} scratch", (long)Math.Max(sizes.buildScratchSize, 256))
        );
        if (!buffer.Ok || !scratch.Ok)
        {
            if (buffer.Ok)
                Free(buffer.Value);

            if (scratch.Ok)
                Free(scratch.Value);

            return Result.Failure($"{name} was not made: {buffer.Error}{scratch.Error}");
        }

        return AddAcceleration(type, buffer.Value, scratch.Value, geometries, counts, name);
    }

    /// <summary>
    /// Makes a structure of <paramref name="type"/> over its <paramref name="buffer"/> and puts it in the heap; if that
    /// fails, it frees the buffers.
    /// </summary>
    private Result<GpuAcceleration> AddAcceleration(
        VkAccelerationStructureTypeKHR type,
        VulkanBuffer buffer,
        VulkanBuffer? scratch,
        VkAccelerationStructureGeometryKHR[] geometries,
        uint[] counts,
        string name
    )
    {
        VkAccelerationStructureCreateInfoKHR createInfo = new()
        {
            buffer = buffer.Buffer,
            offset = 0,
            size = (ulong)buffer.Description.Bytes,
            type = type,
        };
        VkAccelerationStructureKHR handle;
        VkResult result = device.Api.vkCreateAccelerationStructureKHR(&createInfo, null, &handle);
        if (result != VkResult.Success)
        {
            Free(buffer);
            if (scratch is not null)
                Free(scratch);

            return Result.Failure($"Vulkan could not make {name}: {result}.");
        }

        VkAccelerationStructureDeviceAddressInfoKHR addressInfo = new() { accelerationStructure = handle };
        VulkanAcceleration acceleration = new()
        {
            Handle = handle,
            Buffer = buffer,
            Scratch = scratch,
            Type = type,
            Name = name,
            Geometries = geometries,
            PrimitiveCounts = counts,
            Address = device.Api.vkGetAccelerationStructureDeviceAddressKHR(&addressInfo),
        };
        if (!Accelerations.TryAdd(acceleration, out uint id))
        {
            Free(acceleration);

            return Result.Failure($"the heap holds its {VulkanHeap.Accelerations} acceleration structures already.");
        }

        // Only a top-level structure is traced through; a bottom-level one is reached by its address.
        if (type == VkAccelerationStructureTypeKHR.TopLevel)
            heap.WriteAcceleration(id, handle);

        Name(VkObjectType.AccelerationStructureKHR, handle.Handle, name);

        return Result.Success(new GpuAcceleration(id, acceleration.Address));
    }

    private void Free(VulkanAcceleration acceleration)
    {
        device.Api.vkDestroyAccelerationStructureKHR(acceleration.Handle, null);
        Free(acceleration.Buffer);
        if (acceleration.Scratch is { } scratch)
            Free(scratch);
    }

    private void Free(VulkanTexture texture)
    {
        foreach (VkImageView view in texture.LevelViews)
        {
            if (view != texture.View)
                device.Api.vkDestroyImageView(view);
        }

        if (texture.View.IsNotNull)
            device.Api.vkDestroyImageView(texture.View);

        if (texture.IsWindow)
            return;

        device.Api.vkDestroyImage(texture.Image);
        FreeMemory(texture.Memory);
    }

    private Result<GpuPipeline> AddPipeline(VkPipeline pipeline, VkPipelineBindPoint bindPoint)
    {
        if (Pipelines.TryAdd(new VulkanPipeline { Pipeline = pipeline, BindPoint = bindPoint }, out uint id))
            return Result.Success(new GpuPipeline(id));

        device.Api.vkDestroyPipeline(pipeline);

        return Result.Failure("the heap holds every pipeline it can already.");
    }

    private Result<VulkanTexture> CreateVulkanTexture(in TextureDescription description)
    {
        VkImageCreateInfo createInfo = new()
        {
            imageType = description.Kind.ToImageType(),
            format = description.Format.ToVulkan(),
            extent = new VkExtent3D(
                (uint)description.Width,
                (uint)description.Height,
                (uint)(description.Kind == TextureKind.Texture3D ? description.Depth : 1)
            ),
            mipLevels = (uint)description.Levels,
            arrayLayers = 1,
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
        // A texture only copied to and from has no view to make: Vulkan refuses one, and nothing reads it through one.
        bool isViewed =
            (
                description.Usage
                & (
                    TextureUsages.Sampled
                    | TextureUsages.Storage
                    | TextureUsages.ColorTarget
                    | TextureUsages.DepthTarget
                )
            ) != 0;
        VulkanTexture texture = new()
        {
            Image = image,
            Memory = memory.Value,
            Description = description,
            LevelViews = new VkImageView[isViewed ? description.Levels : 0],
            Accesses = new GpuAccess[description.Levels],
        };
        if (!isViewed)
            return Result.Success(texture);

        // A view that fails frees the texture with the views made so far; a null one is nothing to Vulkan.
        Result<VkImageView> view = CreateView(image, description, 0, description.Levels);
        if (!view.Ok)
        {
            Free(texture);

            return Result.Failure(view.Error);
        }

        texture.View = view.Value;
        for (int level = 0; level < description.Levels; level++)
        {
            Result<VkImageView> levelView = CreateView(image, description, level, 1);
            if (!levelView.Ok)
            {
                Free(texture);

                return Result.Failure(levelView.Error);
            }

            texture.LevelViews[level] = levelView.Value;
        }

        return Result.Success(texture);
    }

    /// <summary>
    /// Creates a view of <paramref name="levels"/> levels from <paramref name="firstLevel"/>: the whole texture for
    /// sampling, or one level for writing and drawing into.
    /// </summary>
    private Result<VkImageView> CreateView(VkImage image, in TextureDescription description, int firstLevel, int levels)
    {
        VkImageViewCreateInfo createInfo = new()
        {
            image = image,
            viewType = description.Kind.ToViewType(),
            format = description.Format.ToVulkan(),
            components = VkComponentMapping.Identity,
            subresourceRange = new VkImageSubresourceRange(
                description.Format.ToAspect(),
                (uint)firstLevel,
                (uint)levels,
                0,
                1
            ),
        };
        VkResult result = device.Api.vkCreateImageView(&createInfo, out VkImageView view);

        return result == VkResult.Success
            ? Result.Success(view)
            : Result.Failure($"Vulkan could not view the texture {description.Name}: {result}.");
    }

    private Result<VkShaderModule> CreateModule(ReadOnlyMemory<byte> spirv)
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
    /// Gives the object the name that a GPU debugger shows, when the debug extension is there and the name is not
    /// empty.
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
