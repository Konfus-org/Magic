using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// A pipeline, with the heap's layout it reads the heap and pushes its constants through.
/// </summary>
internal sealed class VulkanPipeline(VkPipeline handle, VkPipelineLayout layout, VkPipelineBindPoint bindPoint)
{
    internal VkPipeline Handle { get; } = handle;

    internal VkPipelineLayout Layout { get; } = layout;

    internal VkPipelineBindPoint BindPoint { get; } = bindPoint;
}

/// <summary>
/// Makes pipelines from compiled SPIR-V, each with the heap's layout.
/// </summary>
internal sealed unsafe class VulkanPipelines(VulkanDevice device, VulkanHeap heap)
{
    private readonly VulkanDevice _device = device;
    private readonly VulkanHeap _heap = heap;

    internal Result<VulkanPipeline> CreateGraphics(GpuPipelineDescription description)
    {
        Result fits = CheckConstants(description.Vertex, description.Fragment);

        if (!fits.Ok) return Result.Failure<VulkanPipeline>(fits.Error);

        VkShaderModule vertex = CreateModule(description.Vertex);
        VkShaderModule fragment = CreateModule(description.Fragment);
        try
        {
            if (vertex.IsNull || fragment.IsNull)
                return Result.Failure<VulkanPipeline>("The GPU would not take the shaders' code.");

            VkPipeline pipeline = CreateGraphicsPipeline(description, vertex, fragment, out VkResult result);

            return result == VkResult.Success
                ? Result.Success(new VulkanPipeline(pipeline, _heap.PipelineLayout, VkPipelineBindPoint.Graphics))
                : Result.Failure<VulkanPipeline>($"The GPU would not make the pipeline: {result}.");
        }
        finally
        {
            if (vertex.IsNotNull) _device.Api.vkDestroyShaderModule(vertex);
            if (fragment.IsNotNull) _device.Api.vkDestroyShaderModule(fragment);
        }
    }

    internal Result<VulkanPipeline> CreateCompute(GpuShader compute)
    {
        Result fits = CheckConstants(compute, compute);

        if (!fits.Ok) return Result.Failure<VulkanPipeline>(fits.Error);

        VkShaderModule module = CreateModule(compute);

        if (module.IsNull) return Result.Failure<VulkanPipeline>("The GPU would not take the compute shader's code.");

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
                layout = _heap.PipelineLayout,
            };
            VkPipeline pipeline;
            VkResult result = _device.Api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &info, &pipeline);
            _device.Api.vkDestroyShaderModule(module);

            return result == VkResult.Success
                ? Result.Success(new VulkanPipeline(pipeline, _heap.PipelineLayout, VkPipelineBindPoint.Compute))
                : Result.Failure<VulkanPipeline>($"The GPU would not make the compute pipeline: {result}.");
        }
    }

    /// <summary>
    /// Destroys a pipeline; its layout is the heap's.
    /// </summary>
    internal void Destroy(VulkanPipeline pipeline) => _device.Api.vkDestroyPipeline(pipeline.Handle);

    /// <summary>
    /// Why a pipeline of these stages cannot be made, if one takes more constants than are pushed.
    /// </summary>
    private static Result CheckConstants(GpuShader first, GpuShader second)
        => first.ConstantBytes > VulkanHeap.ConstantBytes || second.ConstantBytes > VulkanHeap.ConstantBytes
            ? Result.Failure(
                $"A shader's constants are over the {VulkanHeap.ConstantBytes} bytes a draw or dispatch pushes.")
            : Result.Success();

    private VkShaderModule CreateModule(GpuShader shader)
    {
        fixed (byte* code = shader.Code.Span)
        {
            VkShaderModuleCreateInfo info = new() { codeSize = (nuint)shader.Code.Length, pCode = (uint*)code };
            VkShaderModule module;

            return _device.Api.vkCreateShaderModule(&info, &module) == VkResult.Success ? module : VkShaderModule.Null;
        }
    }

    private VkPipeline CreateGraphicsPipeline(
        GpuPipelineDescription description,
        VkShaderModule vertex,
        VkShaderModule fragment,
        out VkResult result)
    {
        byte[] vertexEntry = CString.ToBytes(description.Vertex.EntryPoint);
        byte[] fragmentEntry = CString.ToBytes(description.Fragment.EntryPoint);
        int colorCount = description.ColorFormats.Count;
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

            // No vertex buffers: the vertex shader reads its vertices from the heap.
            VkPipelineVertexInputStateCreateInfo vertexInput = new();
            VkPipelineInputAssemblyStateCreateInfo assembly = new() { topology = VkPrimitiveTopology.TriangleList };
            VkPipelineViewportStateCreateInfo viewport = new() { viewportCount = 1, scissorCount = 1 };

            // The viewport is flipped as each pass's is set (VulkanRecorder), so clip space is +Y up and a clockwise
            // triangle in it is clockwise on the target, as the shaders are written for.
            VkPipelineRasterizationStateCreateInfo rasterization = new()
            {
                depthClampEnable = !description.IsDepthClipped,
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
                depthWriteEnable = hasDepth && description.IsDepthWritten,
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
                layout = _heap.PipelineLayout,
            };
            VkPipeline pipeline;
            result = _device.Api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, &pipeline);

            return pipeline;
        }
    }
}
