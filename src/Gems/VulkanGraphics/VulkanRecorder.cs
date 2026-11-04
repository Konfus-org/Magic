using Magic.Debugging;
using Magic.Gpu;
using Vortice.Vulkan;

namespace VulkanGraphics;

/// <summary>
/// Turns a frame's <see cref="GpuCommands"/> into Vulkan commands: a debug group and timestamps around each pass,
/// its transitions as one barrier, its draws inside dynamic rendering, and each command as the call it names. Keeps
/// each texture level's layout as the transitions move it, so a barrier knows what it moves from.
/// </summary>
internal sealed unsafe class VulkanRecorder(VulkanDevice device, VulkanResources resources, VulkanHeap heap)
{
    private const int MaxTargets = 8;

    private readonly List<VkBufferMemoryBarrier2> _bufferBarriers = [];
    private readonly List<VkImageMemoryBarrier2> _imageBarriers = [];

    public void Record(VulkanFrames.Frame frame, in GpuCommands commands)
    {
        VkCommandBuffer cmd = frame.CommandBuffer;
        VkDeviceApi api = device.Api;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, heap.PipelineLayout, 0, heap.Set);
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, heap.PipelineLayout, 0, heap.Set);
        for (int passIndex = 0; passIndex < commands.Passes.Length; passIndex++)
        {
            GpuPass pass = commands.Passes[passIndex];
            BeginGroup(cmd, pass.Name);
            Transition(cmd, pass.Transitions.AsSpan());
            frame.WriteTimestamp(device, pass.Name, passIndex);
            bool isDrawing = pass.Kind == GpuPassKind.Draw && BeginRendering(cmd, pass);
            foreach (GpuCommand command in pass.Commands)
            {
                Run(frame, passIndex, command);
            }

            if (isDrawing)
                api.vkCmdEndRendering(cmd);

            frame.WriteTimestamp(device, pass.Name, passIndex);
            EndGroup(cmd);
        }
    }

    /// <summary>
    /// Moves every level of a texture to <paramref name="access"/>, as the end of a frame needs the window's image shown.
    /// </summary>
    public void TransitionTo(VkCommandBuffer cmd, VulkanTexture texture, GpuAccess access)
    {
        _imageBarriers.Clear();
        AddImageBarrier(texture, -1, GpuAccess.ShaderWrite, access);
        Flush(cmd);
    }

    private static VkImageSubresourceRange GetRange(VulkanTexture texture, int level)
    {
        VkImageAspectFlags aspect = texture.Description.Format.IsDepth
            ? VkImageAspectFlags.Depth
            : VkImageAspectFlags.Color;
        uint layers = (uint)(
            texture.Description.Kind == TextureKind.Cube ? 6
            : texture.Description.Kind == TextureKind.Texture2DArray ? texture.Description.Layers
            : 1
        );

        return level < 0
            ? new VkImageSubresourceRange(aspect, 0, (uint)texture.Description.Levels, 0, layers)
            : new VkImageSubresourceRange(aspect, (uint)level, 1, 0, layers);
    }

    /// <summary>
    /// Says a copy was not recorded: one of what it names has left the heap, so what it would have moved is lost.
    /// </summary>
    private static void LogMissing(string kind, in GpuCommand command) =>
        Log.Error(
            $"A {kind} of {command.Size} bytes from {command.Id} to {command.Id2} names a resource that has left the heap."
        );

    private static VkImageSubresourceLayers GetLayers(VulkanTexture texture, uint level, uint layer) =>
        new(texture.Description.Format.IsDepth ? VkImageAspectFlags.Depth : VkImageAspectFlags.Color, level, layer, 1);

    private static VkExtent3D GetExtent(VulkanTexture texture, uint level) =>
        new(
            Math.Max(1u, (uint)texture.Description.Width >> (int)level),
            Math.Max(1u, (uint)texture.Description.Height >> (int)level),
            Math.Max(1u, (uint)texture.Description.Depth >> (int)level)
        );

    private void BeginGroup(VkCommandBuffer cmd, string name)
    {
        if (!device.HasDebugUtils)
            return;

        using NativeStrings label = new([name]);
        VkDebugUtilsLabelEXT info = new() { pLabelName = label.Pointers[0] };
        device.InstanceApi.vkCmdBeginDebugUtilsLabelEXT(cmd, &info);
    }

    private void EndGroup(VkCommandBuffer cmd)
    {
        if (device.HasDebugUtils)
            device.InstanceApi.vkCmdEndDebugUtilsLabelEXT(cmd);
    }

    private void Transition(VkCommandBuffer cmd, ReadOnlySpan<GpuTransition> transitions)
    {
        _bufferBarriers.Clear();
        _imageBarriers.Clear();
        foreach (GpuTransition transition in transitions)
        {
            if (transition.IsTexture)
            {
                if (resources.GetTexture(transition.Id) is { } texture)
                    AddImageBarrier(texture, transition.Level, transition.Before, transition.After);
                else
                    Log.Warn($"A transition names texture {transition.Id}, which is gone.");
            }
            else if (resources.GetBuffer(transition.Id) is { } buffer)
            {
                (VkPipelineStageFlags2 beforeStages, VkAccessFlags2 beforeAccess) =
                    transition.Before.ToStageAndAccess();
                (VkPipelineStageFlags2 afterStages, VkAccessFlags2 afterAccess) = transition.After.ToStageAndAccess();
                _bufferBarriers.Add(
                    new VkBufferMemoryBarrier2(buffer.Buffer, beforeStages, beforeAccess, afterStages, afterAccess)
                );
            }
            else
            {
                Log.Warn($"A transition names buffer {transition.Id}, which is gone.");
            }
        }

        Flush(cmd);
    }

    /// <summary>
    /// A barrier for one level, or for every level that shares a layout, from the layout the level is in now.
    /// </summary>
    private void AddImageBarrier(VulkanTexture texture, int level, GpuAccess before, GpuAccess after)
    {
        bool isDepth = texture.Description.Format.IsDepth;
        VkImageLayout newLayout = after.ToLayout(isDepth);
        (VkPipelineStageFlags2 beforeStages, VkAccessFlags2 beforeAccess) = before.ToStageAndAccess();
        (VkPipelineStageFlags2 afterStages, VkAccessFlags2 afterAccess) = after.ToStageAndAccess();
        int first = level < 0 ? 0 : level;
        int last = level < 0 ? texture.Layouts.Length - 1 : level;
        int runStart = first;
        for (int i = first; i <= last; i++)
        {
            bool isRunEnd = i == last || texture.Layouts[i + 1] != texture.Layouts[runStart];
            if (!isRunEnd)
                continue;

            VkImageLayout oldLayout = texture.Layouts[runStart];
            VkImageSubresourceRange range = GetRange(texture, -1) with
            {
                baseMipLevel = (uint)runStart,
                levelCount = (uint)(i - runStart + 1),
            };
            _imageBarriers.Add(
                new VkImageMemoryBarrier2(
                    texture.Image,
                    range,
                    beforeStages,
                    beforeAccess,
                    afterStages,
                    afterAccess,
                    oldLayout,
                    newLayout
                )
            );
            for (int moved = runStart; moved <= i; moved++)
            {
                texture.Layouts[moved] = newLayout;
            }

            runStart = i + 1;
        }
    }

    private void Flush(VkCommandBuffer cmd)
    {
        if (_bufferBarriers.Count == 0 && _imageBarriers.Count == 0)
            return;

        Span<VkBufferMemoryBarrier2> buffers = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(
            _bufferBarriers
        );
        Span<VkImageMemoryBarrier2> images = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_imageBarriers);
        fixed (VkBufferMemoryBarrier2* buffersPointer = buffers)
        fixed (VkImageMemoryBarrier2* imagesPointer = images)
        {
            VkDependencyInfo dependency = new()
            {
                bufferMemoryBarrierCount = (uint)buffers.Length,
                pBufferMemoryBarriers = buffersPointer,
                imageMemoryBarrierCount = (uint)images.Length,
                pImageMemoryBarriers = imagesPointer,
            };
            device.Api.vkCmdPipelineBarrier2(cmd, &dependency);
        }
    }

    /// <summary>
    /// Begins drawing into the pass's targets, the viewport and scissor the whole of them; false when a target is
    /// gone, which skips the pass's draws.
    /// </summary>
    private bool BeginRendering(VkCommandBuffer cmd, in GpuPass pass)
    {
        GpuRenderTargets targets = pass.Targets;
        if (targets.Colors.Length > MaxTargets)
        {
            Log.Error($"The pass {pass.Name} draws into {targets.Colors.Length} targets; at most {MaxTargets} can be.");

            return false;
        }

        VkRenderingAttachmentInfo* colors = stackalloc VkRenderingAttachmentInfo[MaxTargets];
        uint width = 0;
        uint height = 0;
        for (int i = 0; i < targets.Colors.Length; i++)
        {
            GpuColorTarget color = targets.Colors[i];
            if (resources.GetTexture(color.Texture.Id) is not { } texture)
            {
                Log.Error($"The pass {pass.Name} draws into texture {color.Texture.Id}, which is gone.");

                return false;
            }

            VkExtent3D extent = GetExtent(texture, (uint)color.Level);
            width = extent.width;
            height = extent.height;
            colors[i] = new VkRenderingAttachmentInfo
            {
                imageView = texture.LevelViews[color.Level],
                imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = color.Load.ToVulkan(),
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue(
                    BitConverter.UInt32BitsToSingle(color.ClearColor.X),
                    BitConverter.UInt32BitsToSingle(color.ClearColor.Y),
                    BitConverter.UInt32BitsToSingle(color.ClearColor.Z),
                    BitConverter.UInt32BitsToSingle(color.ClearColor.W)
                ),
            };
        }

        VkRenderingAttachmentInfo depth = new();
        if (targets.Depth is { } depthTarget)
        {
            if (resources.GetTexture(depthTarget.Texture.Id) is not { } texture)
            {
                Log.Error($"The pass {pass.Name} draws depth into texture {depthTarget.Texture.Id}, which is gone.");

                return false;
            }

            VkExtent3D extent = GetExtent(texture, (uint)depthTarget.Level);
            width = extent.width;
            height = extent.height;
            depth = new VkRenderingAttachmentInfo
            {
                imageView = texture.LevelViews[depthTarget.Level],
                imageLayout = VkImageLayout.DepthAttachmentOptimal,
                loadOp = depthTarget.Load.ToVulkan(),
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue(depthTarget.ClearDepth, 0),
            };
        }

        if (targets.Colors.Length == 0 && targets.Depth is null)
        {
            width = (uint)targets.Width;
            height = (uint)targets.Height;
        }

        if (width == 0 || height == 0)
        {
            Log.Error($"The pass {pass.Name} draws into nothing.");

            return false;
        }

        VkRenderingInfo rendering = new()
        {
            renderArea = new VkRect2D(0, 0, width, height),
            layerCount = 1,
            colorAttachmentCount = (uint)targets.Colors.Length,
            pColorAttachments = colors,
            pDepthAttachment = targets.Depth is null ? null : &depth,
        };
        device.Api.vkCmdBeginRendering(cmd, &rendering);
        SetViewport(cmd, 0, 0, width, height);
        device.Api.vkCmdSetScissor(cmd, 0, 0, width, height);

        return true;
    }

    /// <summary>
    /// A viewport with +Y up: Vulkan's grows downward, so it is flipped, which keeps clockwise triangles clockwise.
    /// </summary>
    private void SetViewport(VkCommandBuffer cmd, uint x, uint y, uint width, uint height) =>
        device.Api.vkCmdSetViewport(cmd, x, y + height, width, -(float)height, 0, 1);

    private void Run(VulkanFrames.Frame frame, int passIndex, in GpuCommand command)
    {
        VkCommandBuffer cmd = frame.CommandBuffer;
        VkDeviceApi api = device.Api;
        switch (command.Kind)
        {
            case GpuCommandKind.BindPipeline:
                if (resources.GetPipeline(command.Id) is { } pipeline)
                    api.vkCmdBindPipeline(cmd, pipeline.BindPoint, pipeline.Pipeline);
                else
                    Log.Warn($"Pipeline {command.Id} is gone and is not bound.");

                break;
            case GpuCommandKind.PushConstants:
            {
                GpuConstants constants = command.Constants;
                api.vkCmdPushConstants(
                    cmd,
                    heap.PipelineLayout,
                    VulkanHeap.PushStages,
                    0,
                    VulkanHeap.ConstantsBytes,
                    &constants
                );

                break;
            }

            case GpuCommandKind.Dispatch:
                api.vkCmdDispatch(cmd, command.X, command.Y, command.Z);
                break;
            case GpuCommandKind.DispatchIndirect:
                if (resources.GetBuffer(command.Id) is { } arguments)
                    api.vkCmdDispatchIndirect(cmd, arguments.Buffer, command.Offset);

                break;
            case GpuCommandKind.SetViewport:
                if (command.Z > 0 && command.W > 0)
                    SetViewport(cmd, command.X, command.Y, command.Z, command.W);

                break;
            case GpuCommandKind.SetScissor:
                if (command.Z > 0 && command.W > 0)
                    api.vkCmdSetScissor(cmd, (int)command.X, (int)command.Y, command.Z, command.W);

                break;
            case GpuCommandKind.BindIndexBuffer:
                if (resources.GetBuffer(command.Id) is { } indices)
                    api.vkCmdBindIndexBuffer(cmd, indices.Buffer, command.Offset, VkIndexType.Uint32);

                break;
            case GpuCommandKind.Draw:
                api.vkCmdDraw(cmd, command.X, command.Y, command.Z, command.W);
                break;
            case GpuCommandKind.DrawIndexed:
                api.vkCmdDrawIndexed(cmd, command.X, command.Y, command.Z, 0, command.W);
                break;
            case GpuCommandKind.DrawIndirectCount:
                if (resources.GetBuffer(command.Id) is { } plain && resources.GetBuffer(command.Id2) is { } plainCount)
                {
                    api.vkCmdDrawIndirectCount(
                        cmd,
                        plain.Buffer,
                        command.Offset,
                        plainCount.Buffer,
                        command.Offset2,
                        command.X,
                        command.Y
                    );
                }

                break;
            case GpuCommandKind.DrawIndexedIndirectCount:
                if (resources.GetBuffer(command.Id) is { } draws && resources.GetBuffer(command.Id2) is { } count)
                {
                    api.vkCmdDrawIndexedIndirectCount(
                        cmd,
                        draws.Buffer,
                        command.Offset,
                        count.Buffer,
                        command.Offset2,
                        command.X,
                        command.Y
                    );
                }

                break;
            case GpuCommandKind.CopyBuffer:
                if (
                    resources.GetBuffer(command.Id) is { } source
                    && resources.GetBuffer(command.Id2) is { } destination
                )
                {
                    VkBufferCopy region = new()
                    {
                        srcOffset = command.Offset,
                        dstOffset = command.Offset2,
                        size = command.Size,
                    };
                    api.vkCmdCopyBuffer(cmd, source.Buffer, destination.Buffer, 1, &region);
                }
                else
                    LogMissing("CopyBuffer", command);

                break;
            case GpuCommandKind.CopyBufferToTexture:
                if (resources.GetBuffer(command.Id) is { } from && resources.GetTexture(command.Id2) is { } into)
                {
                    VkBufferImageCopy region = new()
                    {
                        bufferOffset = command.Offset,
                        imageSubresource = GetLayers(into, command.X, command.Y),
                        imageExtent = GetExtent(into, command.X),
                    };
                    api.vkCmdCopyBufferToImage(
                        cmd,
                        from.Buffer,
                        into.Image,
                        VkImageLayout.TransferDstOptimal,
                        1,
                        &region
                    );
                }
                else
                    LogMissing("CopyBufferToTexture", command);

                break;
            case GpuCommandKind.CopyTextureToBuffer:
                if (resources.GetTexture(command.Id) is { } read && resources.GetBuffer(command.Id2) is { } to)
                {
                    VkBufferImageCopy region = new()
                    {
                        bufferOffset = command.Offset2,
                        imageSubresource = GetLayers(read, command.X, command.Y),
                        imageExtent = GetExtent(read, command.X),
                    };
                    api.vkCmdCopyImageToBuffer(
                        cmd,
                        read.Image,
                        VkImageLayout.TransferSrcOptimal,
                        to.Buffer,
                        1,
                        &region
                    );
                }
                else
                    LogMissing("CopyTextureToBuffer", command);

                break;
            case GpuCommandKind.CopyTexture:
                if (resources.GetTexture(command.Id) is { } copied && resources.GetTexture(command.Id2) is { } onto)
                {
                    VkImageCopy region = new()
                    {
                        srcSubresource = GetLayers(copied, 0, 0),
                        dstSubresource = GetLayers(onto, 0, 0),
                        extent = GetExtent(copied, 0),
                    };
                    api.vkCmdCopyImage(
                        cmd,
                        copied.Image,
                        VkImageLayout.TransferSrcOptimal,
                        onto.Image,
                        VkImageLayout.TransferDstOptimal,
                        1,
                        &region
                    );
                }
                else
                    LogMissing("CopyTexture", command);

                break;
            case GpuCommandKind.BlitTexture:
                if (resources.GetTexture(command.Id) is { } blitted && resources.GetTexture(command.Id2) is { } over)
                {
                    VkExtent3D sourceExtent = GetExtent(blitted, 0);
                    VkExtent3D targetExtent = GetExtent(over, 0);
                    VkImageBlit region = new()
                    {
                        srcSubresource = GetLayers(blitted, 0, 0),
                        dstSubresource = GetLayers(over, 0, 0),
                    };
                    region.srcOffsets[1] = new VkOffset3D((int)sourceExtent.width, (int)sourceExtent.height, 1);
                    region.dstOffsets[1] = new VkOffset3D((int)targetExtent.width, (int)targetExtent.height, 1);
                    api.vkCmdBlitImage(
                        cmd,
                        blitted.Image,
                        VkImageLayout.TransferSrcOptimal,
                        over.Image,
                        VkImageLayout.TransferDstOptimal,
                        1,
                        &region,
                        VkFilter.Linear
                    );
                }

                break;
            case GpuCommandKind.FillBuffer:
                if (resources.GetBuffer(command.Id) is { } filled)
                    api.vkCmdFillBuffer(
                        cmd,
                        filled.Buffer,
                        command.Offset,
                        command.Size == 0 ? ulong.MaxValue : command.Size,
                        command.X
                    );

                break;
            case GpuCommandKind.ClearTexture:
                if (resources.GetTexture(command.Id) is { } cleared)
                    Clear(cmd, cleared, command.Constants);

                break;
            case GpuCommandKind.Timestamp:
                frame.WriteTimestamp(device, command.Label, passIndex);
                break;
            case GpuCommandKind.BuildAcceleration:
                if (resources.GetAcceleration(command.Id) is { } acceleration)
                    Build(cmd, acceleration, command.X);
                else
                    Log.Warn($"Acceleration structure {command.Id} is gone and is not built.");

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "There is no such command.");
        }
    }

    /// <summary>
    /// Builds an acceleration structure between two barriers over everything: the copies that filled what it is
    /// built over land first, and whatever traces through it afterwards waits for it.
    /// </summary>
    private void Build(VkCommandBuffer cmd, VulkanAcceleration acceleration, uint instanceCount)
    {
        VkMemoryBarrier2 before = new()
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands,
            srcAccessMask = VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead,
            dstStageMask = VkPipelineStageFlags2.AccelerationStructureBuildKHR,
            dstAccessMask =
                VkAccessFlags2.AccelerationStructureReadKHR
                | VkAccessFlags2.AccelerationStructureWriteKHR
                | VkAccessFlags2.ShaderRead,
        };
        VkDependencyInfo beforeInfo = new() { memoryBarrierCount = 1, pMemoryBarriers = &before };
        device.Api.vkCmdPipelineBarrier2(cmd, &beforeInfo);

        int count = acceleration.Geometries.Length;
        VkAccelerationStructureBuildRangeInfoKHR[] ranges = new VkAccelerationStructureBuildRangeInfoKHR[count];
        for (int i = 0; i < count; i++)
        {
            uint primitives =
                acceleration.Type == VkAccelerationStructureTypeKHR.TopLevel
                    ? Math.Min(instanceCount, acceleration.PrimitiveCounts[i])
                    : acceleration.PrimitiveCounts[i];
            ranges[i] = new VkAccelerationStructureBuildRangeInfoKHR { primitiveCount = primitives };
        }

        fixed (VkAccelerationStructureGeometryKHR* geometries = acceleration.Geometries)
        fixed (VkAccelerationStructureBuildRangeInfoKHR* rangesPointer = ranges)
        {
            VkAccelerationStructureBuildGeometryInfoKHR info = new()
            {
                type = acceleration.Type,
                flags = VkBuildAccelerationStructureFlagsKHR.PreferFastTrace,
                mode = VkBuildAccelerationStructureModeKHR.Build,
                dstAccelerationStructure = acceleration.Handle,
                geometryCount = (uint)count,
                pGeometries = geometries,
                scratchData = new VkDeviceOrHostAddressKHR { deviceAddress = acceleration.Scratch.Address },
            };
            VkAccelerationStructureBuildRangeInfoKHR* rangesList = rangesPointer;
            device.Api.vkCmdBuildAccelerationStructuresKHR(cmd, 1, &info, &rangesList);
        }

        VkMemoryBarrier2 after = new()
        {
            srcStageMask = VkPipelineStageFlags2.AccelerationStructureBuildKHR,
            srcAccessMask = VkAccessFlags2.AccelerationStructureWriteKHR,
            dstStageMask = VkPipelineStageFlags2.AllCommands,
            dstAccessMask = VkAccessFlags2.AccelerationStructureReadKHR | VkAccessFlags2.ShaderRead,
        };
        VkDependencyInfo afterInfo = new() { memoryBarrierCount = 1, pMemoryBarriers = &after };
        device.Api.vkCmdPipelineBarrier2(cmd, &afterInfo);
    }

    private void Clear(VkCommandBuffer cmd, VulkanTexture texture, in GpuConstants constants)
    {
        VkImageSubresourceRange range = GetRange(texture, -1);
        if (texture.Description.Format.IsDepth)
        {
            VkClearDepthStencilValue depth = new(BitConverter.UInt32BitsToSingle(constants.X), 0);
            device.Api.vkCmdClearDepthStencilImage(
                cmd,
                texture.Image,
                VkImageLayout.TransferDstOptimal,
                &depth,
                1,
                &range
            );

            return;
        }

        VkClearColorValue color = texture.Description.Format.IsInteger
            ? new VkClearColorValue(constants.X, constants.Y, constants.Z, constants.W)
            : new VkClearColorValue(
                BitConverter.UInt32BitsToSingle(constants.X),
                BitConverter.UInt32BitsToSingle(constants.Y),
                BitConverter.UInt32BitsToSingle(constants.Z),
                BitConverter.UInt32BitsToSingle(constants.W)
            );
        device.Api.vkCmdClearColorImage(cmd, texture.Image, VkImageLayout.TransferDstOptimal, &color, 1, &range);
    }
}
