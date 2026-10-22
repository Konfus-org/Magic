using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

internal enum SetKind : byte
{
    Read,
    Write,
    Uniform,
}

/// <summary>
/// What a shader stage reads, writes or takes as constants in one descriptor set, as the binding model lays them out
/// (Shaders/Include/Bindings.hlsli): a read set's sampled textures first, each with its sampler at the same binding,
/// then the textures it reads texel by texel, which a shader declares as read-only textures and so as sampled images,
/// then its storage buffers; a write set's storage images, then its storage buffers; or the stage's one constant block.
/// </summary>
internal readonly record struct SetShape(SetKind Kind, uint Sampled, uint Textures, uint Buffers)
{
    internal static SetShape Empty => new(SetKind.Read, 0, 0, 0);

    internal bool IsEmpty => Sampled + Textures + Buffers == 0;
}

/// <summary>
/// A pipeline, its layout, and the shape of each of its descriptor sets by set number: a graphics pipeline's vertex
/// stage reads set 0 and takes constants from set 1, its fragment stage sets 2 and 3; a compute pipeline reads set 0,
/// writes set 1 and takes constants from set 2, as the binding header, which the shaders are compiled for, lays them out.
/// </summary>
internal sealed class VulkanPipeline(
    VkPipeline handle,
    VkPipelineLayout layout,
    VkPipelineBindPoint bindPoint,
    SetShape[] sets)
{
    internal VkPipeline Handle { get; } = handle;

    internal VkPipelineLayout Layout { get; } = layout;

    internal VkPipelineBindPoint BindPoint { get; } = bindPoint;

    internal SetShape[] Sets { get; } = sets;
}

/// <summary>
/// Makes pipelines from compiled SPIR-V, the descriptor set layouts they share kept by shape.
/// </summary>
internal sealed unsafe class VulkanPipelines : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly Dictionary<SetShape, VkDescriptorSetLayout> _setLayouts = [];

    public VulkanPipelines(VulkanDevice device)
    {
        _device = device;
        UniformLayout = GetSetLayout(new SetShape(SetKind.Uniform, 0, 0, 1));
    }

    /// <summary>
    /// The layout of every stage's constants set, the same for all: one dynamic uniform buffer, whose offset each draw
    /// names.
    /// </summary>
    internal VkDescriptorSetLayout UniformLayout { get; }

    public void Dispose()
    {
        foreach (VkDescriptorSetLayout layout in _setLayouts.Values)
        {
            _device.Api.vkDestroyDescriptorSetLayout(layout);
        }
    }

    /// <summary>
    /// What kind of descriptor binding <paramref name="binding"/> of a set so shaped holds.
    /// </summary>
    internal static VkDescriptorType GetDescriptorType(SetShape shape, uint binding) => shape.Kind switch
    {
        SetKind.Uniform => VkDescriptorType.UniformBufferDynamic,
        SetKind.Write => binding < shape.Textures ? VkDescriptorType.StorageImage : VkDescriptorType.StorageBuffer,
        SetKind.Read when binding < shape.Sampled => VkDescriptorType.CombinedImageSampler,
        SetKind.Read when binding < shape.Sampled + shape.Textures => VkDescriptorType.SampledImage,
        SetKind.Read => VkDescriptorType.StorageBuffer,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape.Kind, "There is no such set."),
    };

    internal Result<VulkanPipeline> CreateGraphics(GpuPipelineDescription description)
    {
        SetShape[] sets =
        [
            ToReadShape(description.Vertex),
            ToUniformShape(description.Vertex),
            ToReadShape(description.Fragment),
            ToUniformShape(description.Fragment),
        ];
        VkShaderModule vertex = CreateModule(description.Vertex);
        VkShaderModule fragment = CreateModule(description.Fragment);
        try
        {
            if (vertex.IsNull || fragment.IsNull)
            {
                return Result.Failure<VulkanPipeline>("The GPU would not take the shaders' code.");
            }

            VkPipelineLayout layout = CreateLayout(sets);
            VkPipeline pipeline = CreateGraphicsPipeline(description, vertex, fragment, layout, out VkResult result);
            if (result != VkResult.Success)
            {
                _device.Api.vkDestroyPipelineLayout(layout);
                return Result.Failure<VulkanPipeline>($"The GPU would not make the pipeline: {result}.");
            }

            return Result.Success(new VulkanPipeline(pipeline, layout, VkPipelineBindPoint.Graphics, sets));
        }
        finally
        {
            if (vertex.IsNotNull) _device.Api.vkDestroyShaderModule(vertex);
            if (fragment.IsNotNull) _device.Api.vkDestroyShaderModule(fragment);
        }
    }

    internal Result<VulkanPipeline> CreateCompute(GpuShader compute)
    {
        SetShape[] sets =
        [
            ToReadShape(compute),
            new SetShape(SetKind.Write, 0, compute.ReadWriteStorageTextures, compute.ReadWriteStorageBuffers),
            ToUniformShape(compute),
        ];
        VkShaderModule module = CreateModule(compute);
        if (module.IsNull) return Result.Failure<VulkanPipeline>("The GPU would not take the compute shader's code.");

        VkPipelineLayout layout = CreateLayout(sets);
        byte[] entryPoint = CString.ToBytes(compute.EntryPoint);
        fixed (byte* entry = entryPoint)
        {
            VkComputePipelineCreateInfo info = new()
            {
                stage = new VkPipelineShaderStageCreateInfo
                {
                    stage = VkShaderStageFlags.Compute,
                    module = module,
                    pName = entry,
                },
                layout = layout,
            };
            VkPipeline pipeline;
            VkResult result = _device.Api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &info, &pipeline);
            _device.Api.vkDestroyShaderModule(module);
            if (result == VkResult.Success)
            {
                return Result.Success(new VulkanPipeline(pipeline, layout, VkPipelineBindPoint.Compute, sets));
            }

            _device.Api.vkDestroyPipelineLayout(layout);
            return Result.Failure<VulkanPipeline>($"The GPU would not make the compute pipeline: {result}.");
        }
    }

    internal void Destroy(VulkanPipeline pipeline)
    {
        _device.Api.vkDestroyPipeline(pipeline.Handle);
        _device.Api.vkDestroyPipelineLayout(pipeline.Layout);
    }

    /// <summary>
    /// The layout of a set so shaped, made the first time a pipeline needs it: every binding visible to every stage, so
    /// one layout serves whichever stage reads it.
    /// </summary>
    internal VkDescriptorSetLayout GetSetLayout(SetShape shape)
    {
        if (_setLayouts.TryGetValue(shape, out VkDescriptorSetLayout found)) return found;

        uint count = shape.Sampled + shape.Textures + shape.Buffers;
        VkDescriptorSetLayoutBinding* bindings = stackalloc VkDescriptorSetLayoutBinding[(int)Math.Max(count, 1)];
        for (uint i = 0; i < count; i++)
        {
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = i,
                descriptorType = GetDescriptorType(shape, i),
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.All,
            };
        }

        VkDescriptorSetLayoutCreateInfo info = new() { bindingCount = count, pBindings = bindings };
        VkDescriptorSetLayout layout;
        _device.Api.vkCreateDescriptorSetLayout(&info, &layout);
        _setLayouts[shape] = layout;
        return layout;
    }

    private static SetShape ToReadShape(GpuShader shader)
        => new(SetKind.Read, shader.Samplers, shader.StorageTextures, shader.StorageBuffers);

    private static SetShape ToUniformShape(GpuShader shader)
        => shader.UniformBuffers > 0 ? new SetShape(SetKind.Uniform, 0, 0, 1) : SetShape.Empty;

    private VkShaderModule CreateModule(GpuShader shader)
    {
        fixed (byte* code = shader.Code.Span)
        {
            VkShaderModuleCreateInfo info = new() { codeSize = (nuint)shader.Code.Length, pCode = (uint*)code };
            VkShaderModule module;
            return _device.Api.vkCreateShaderModule(&info, &module) == VkResult.Success ? module : VkShaderModule.Null;
        }
    }

    /// <summary>
    /// A pipeline layout of every set up to the last a stage uses, an empty set standing in for one no stage uses.
    /// </summary>
    private VkPipelineLayout CreateLayout(SetShape[] sets)
    {
        int count = sets.Length;
        while (count > 0 && sets[count - 1].IsEmpty)
        {
            count--;
        }

        VkDescriptorSetLayout* layouts = stackalloc VkDescriptorSetLayout[Math.Max(count, 1)];
        for (int i = 0; i < count; i++)
        {
            layouts[i] = sets[i].Kind == SetKind.Uniform ? UniformLayout : GetSetLayout(sets[i]);
        }

        VkPipelineLayoutCreateInfo info = new() { setLayoutCount = (uint)count, pSetLayouts = layouts };
        VkPipelineLayout layout;
        _device.Api.vkCreatePipelineLayout(&info, &layout);
        return layout;
    }

    private VkPipeline CreateGraphicsPipeline(
        GpuPipelineDescription description,
        VkShaderModule vertex,
        VkShaderModule fragment,
        VkPipelineLayout layout,
        out VkResult result)
    {
        byte[] vertexEntry = CString.ToBytes(description.Vertex.EntryPoint);
        byte[] fragmentEntry = CString.ToBytes(description.Fragment.EntryPoint);
        int bufferCount = description.VertexBuffers.Count;
        int inputCount = description.VertexInputs.Count;
        int colorCount = description.ColorFormats.Count;
        VkVertexInputBindingDescription* buffers = stackalloc VkVertexInputBindingDescription[Math.Max(bufferCount, 1)];
        for (int i = 0; i < bufferCount; i++)
        {
            GpuVertexBuffer buffer = description.VertexBuffers[i];
            buffers[i] = new VkVertexInputBindingDescription
            {
                binding = buffer.Slot,
                stride = buffer.Stride,
                inputRate = buffer.PerInstance ? VkVertexInputRate.Instance : VkVertexInputRate.Vertex,
            };
        }

        VkVertexInputAttributeDescription* inputs = stackalloc VkVertexInputAttributeDescription[Math.Max(
            inputCount,
            1)];
        for (int i = 0; i < inputCount; i++)
        {
            GpuVertexInput input = description.VertexInputs[i];
            inputs[i] = new VkVertexInputAttributeDescription
            {
                location = input.Location,
                binding = input.Slot,
                format = input.Format.ToVulkan(),
                offset = input.Offset,
            };
        }

        VkFormat* colorFormats = stackalloc VkFormat[Math.Max(colorCount, 1)];
        VkPipelineColorBlendAttachmentState* blends = stackalloc VkPipelineColorBlendAttachmentState[Math.Max(
            colorCount,
            1)];
        for (int i = 0; i < colorCount; i++)
        {
            colorFormats[i] = description.ColorFormats[i].ToVulkan();
            blends[i] = (i < description.Blends.Count ? description.Blends[i] : GpuBlend.None).ToVulkan();
        }

        bool hasDepth = description.DepthFormat != GpuFormat.Invalid;
        VkDynamicState* dynamics = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        fixed (byte* vertexName = vertexEntry)
        fixed (byte* fragmentName = fragmentEntry)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo
            {
                stage = VkShaderStageFlags.Vertex,
                module = vertex,
                pName = vertexName,
            };
            stages[1] = new VkPipelineShaderStageCreateInfo
            {
                stage = VkShaderStageFlags.Fragment,
                module = fragment,
                pName = fragmentName,
            };
            VkPipelineVertexInputStateCreateInfo vertexInput = new()
            {
                vertexBindingDescriptionCount = (uint)bufferCount,
                pVertexBindingDescriptions = buffers,
                vertexAttributeDescriptionCount = (uint)inputCount,
                pVertexAttributeDescriptions = inputs,
            };
            VkPipelineInputAssemblyStateCreateInfo assembly = new() { topology = VkPrimitiveTopology.TriangleList };
            VkPipelineViewportStateCreateInfo viewport = new() { viewportCount = 1, scissorCount = 1 };

            // The viewport is flipped as each pass's is set (VulkanRecorder), so clip space is +Y up and a clockwise
            // triangle in it is clockwise on the target, as the shaders are written for.
            VkPipelineRasterizationStateCreateInfo rasterization = new()
            {
                depthClampEnable = !description.DepthClip,
                polygonMode = VkPolygonMode.Fill,
                cullMode = description.Cull.ToVulkan(),
                frontFace = VkFrontFace.Clockwise,
                depthBiasEnable = description.DepthBias != 0 || description.DepthBiasSlope != 0,
                depthBiasConstantFactor = description.DepthBias,
                depthBiasClamp = _device.HasDepthBiasClamp ? description.DepthBiasClamp : 0,
                depthBiasSlopeFactor = description.DepthBiasSlope,
                lineWidth = 1,
            };
            VkPipelineMultisampleStateCreateInfo multisample = new()
            {
                rasterizationSamples = VkSampleCountFlags.Count1
            };
            VkPipelineDepthStencilStateCreateInfo depth = new()
            {
                depthTestEnable = hasDepth,
                depthWriteEnable = hasDepth && description.DepthWrite,
                depthCompareOp = description.DepthCompare.ToVulkan(),
            };
            VkPipelineColorBlendStateCreateInfo blend = new()
            {
                attachmentCount = (uint)colorCount,
                pAttachments = blends
            };
            VkPipelineDynamicStateCreateInfo dynamic = new() { dynamicStateCount = 2, pDynamicStates = dynamics };
            VkPipelineRenderingCreateInfo rendering = new()
            {
                colorAttachmentCount = (uint)colorCount,
                pColorAttachmentFormats = colorFormats,
                depthAttachmentFormat = description.DepthFormat.ToVulkan(),
            };
            VkGraphicsPipelineCreateInfo info = new()
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
                layout = layout,
            };
            VkPipeline pipeline;
            result = _device.Api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, &pipeline);
            return pipeline;
        }
    }
}
