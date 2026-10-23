using Magic.Contexts.Gpu;
using Vortice.Vulkan;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.VulkanGpu;

/// <summary>
/// Turns a frame's commands, the plain data <see cref="GpuCommands"/> holds, into a Vulkan command buffer, running them
/// as the renderer and its shaders are written for:
/// <list type="bullet">
/// <item>A pass ends where everything it wrote is done before anything after it uses it. Before a pass begins, every
/// resource its commands touch (its targets, what it says it reads and writes, its index and argument buffers) is
/// waited for and put in the layout the pass uses, in one barrier.</item>
/// <item>A pipeline binds the heap as it is bound, and its constants are pushed before a draw or dispatch when they
/// changed.</item>
/// <item>The viewport is flipped, so clip space is +Y up, as the shaders are written for, and a pass starts with the
/// viewport and the scissor over its whole target.</item>
/// <item>A pass into a window draws into the image the window acquired this frame, acquired the first time one does;
/// a window with no image, minimised, has its passes skipped.</item>
/// </list>
/// </summary>
internal sealed unsafe class VulkanRecorder(
    VulkanDevice device,
    VulkanResources resources,
    VulkanWindows windows,
    VulkanBarriers barriers,
    VulkanTimer timer,
    VulkanHeap heap)
{
    private const uint ArgumentsStride = 20;

    private const VkPipelineStageFlags2 GraphicsShaders = VkPipelineStageFlags2.VertexShader
        | VkPipelineStageFlags2.FragmentShader;

    private readonly VulkanDevice _device = device;
    private readonly VulkanResources _resources = resources;
    private readonly VulkanWindows _windows = windows;
    private readonly VulkanBarriers _barriers = barriers;
    private readonly VulkanTimer _timer = timer;
    private readonly VulkanHeap _heap = heap;

    /// <summary>
    /// The constants as last set, and whether they changed, or a pipeline was bound, since they were last pushed.
    /// </summary>
    private readonly byte[] _push = new byte[VulkanHeap.ConstantBytes];
    private int _pushLength;
    private bool _isPushStale;

    private GpuCommands _commands = new();
    private VkCommandBuffer _commandBuffer;
    private int _slot;
    private bool _isTimed;
    private VulkanPipeline? _pipeline;

    /// <summary>
    /// The render pass draws into a window with no image this frame: its commands are skipped to its end.
    /// </summary>
    private bool _isSkipping;

    /// <summary>
    /// How deep in debug groups the commands are, and whether the top-level one is timed.
    /// </summary>
    private int _depth;
    private bool _isGroupTimed;

    /// <summary>
    /// Records <paramref name="commands"/> into <paramref name="commandBuffer"/> for frame slot
    /// <paramref name="slot"/>, whose images the windows acquire; each top-level debug group timed when
    /// <paramref name="isTimed"/>.
    /// </summary>
    internal void Record(GpuCommands commands, VkCommandBuffer commandBuffer, int slot, bool isTimed)
    {
        _commands = commands;
        _commandBuffer = commandBuffer;
        _slot = slot;
        _isTimed = isTimed;
        _pipeline = null;
        _depth = 0;
        _pushLength = 0;
        _isPushStale = false;
        ReadOnlySpan<GpuCommand> list = commands.Commands;
        for (int i = 0; i < list.Length; i++)
        {
            if (_isSkipping && list[i].Type != GpuCommandType.EndRenderPass) continue;

            Run(list, i);
        }
    }

    private static int FindEnd(ReadOnlySpan<GpuCommand> list, int begin, GpuCommandType end)
    {
        for (int i = begin + 1; i < list.Length; i++)
        {
            if (list[i].Type == end) return i;
        }

        return list.Length;
    }

    /// <summary>
    /// One recorded command, as its Vulkan calls.
    /// </summary>
    private void Run(ReadOnlySpan<GpuCommand> list, int index)
    {
        ref readonly GpuCommand command = ref list[index];
        switch (command.Type)
        {
            case GpuCommandType.BeginRenderPass:
                BeginRenderPass(list, index);
                break;
            case GpuCommandType.EndRenderPass:
                if (!_isSkipping) _device.Api.vkCmdEndRendering(_commandBuffer);

                _isSkipping = false;
                break;
            case GpuCommandType.BeginComputePass:
                BeginComputePass(list, index);
                break;
            case GpuCommandType.EndComputePass:
                break;
            case GpuCommandType.Viewport:
                SetViewport(command.Area);
                break;
            case GpuCommandType.Scissor:
                SetScissor(command.Area);
                break;
            case GpuCommandType.Pipeline:
                BindPipeline(command.Pipeline);
                break;
            case GpuCommandType.IndexBuffer:
                if (_resources.Buffers[command.Buffer.Id] is { } indices)
                {
                    VkIndexType type = command.IndexSize == GpuIndexSize.Bits32
                        ? VkIndexType.Uint32
                        : VkIndexType.Uint16;
                    _device.Api.vkCmdBindIndexBuffer(_commandBuffer, indices.Handle, 0, type);
                }

                break;
            case GpuCommandType.Constants:
                SetConstants(command);
                break;
            case GpuCommandType.Reads or GpuCommandType.Writes:
                // Waited for as the pass began.
                break;
            case GpuCommandType.Draw:
                PushConstants();
                _device.Api.vkCmdDraw(
                    _commandBuffer,
                    command.Count,
                    command.Instances,
                    command.First,
                    command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexed:
                PushConstants();
                _device.Api.vkCmdDrawIndexed(
                    _commandBuffer,
                    command.Count,
                    command.Instances,
                    command.First,
                    command.VertexOffset,
                    command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexedFromBuffer:
                PushConstants();
                if (_resources.Buffers[command.Buffer.Id] is { } draws)
                {
                    _device.Api.vkCmdDrawIndexedIndirect(
                        _commandBuffer,
                        draws.Handle,
                        command.Offset,
                        command.Count,
                        ArgumentsStride);
                }

                break;
            case GpuCommandType.Dispatch:
                PushConstants();
                _device.Api.vkCmdDispatch(_commandBuffer, command.GroupsX, command.GroupsY, command.GroupsZ);
                break;
            case GpuCommandType.DispatchFromBuffer:
                PushConstants();
                if (_resources.Buffers[command.Buffer.Id] is { } groups)
                    _device.Api.vkCmdDispatchIndirect(_commandBuffer, groups.Handle, command.Offset);

                break;
            case GpuCommandType.CopyTexture:
                CopyTexture(command);
                break;
            case GpuCommandType.CopyBuffer:
                CopyBuffer(command);
                break;
            case GpuCommandType.GenerateMipmaps:
                if (_resources.Textures[command.Texture.Id] is { } texture) GenerateMipmaps(texture);

                break;
            case GpuCommandType.BeginDebugGroup:
                BeginDebugGroup(command);
                break;
            case GpuCommandType.EndDebugGroup:
                EndDebugGroup();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(list), command.Type, "There is no such GPU command.");
        }
    }

    /// <summary>
    /// Acquires the images of the windows the pass draws into, waits for what the pass uses, and begins rendering into
    /// its targets, with the viewport and scissor over them whole.
    /// </summary>
    private void BeginRenderPass(ReadOnlySpan<GpuCommand> list, int index)
    {
        ref readonly GpuCommand begin = ref list[index];
        ReadOnlySpan<GpuTextureLevel> colors = _commands.GetLevels(begin);
        foreach (GpuTextureLevel color in colors)
        {
            // A headless window's target has no window and no image: nothing draws into it.
            if (_resources.Textures[color.Texture.Id] is not { } target) continue;

            if (target.Window is { } window ? _windows.TryAcquire(window, _slot) : target.Image.IsNotNull) continue;

            _isSkipping = true;

            return;
        }

        bool isDiscard = begin.Contents != GpuContents.Keep;
        VkAccessFlags2 colorAccess = VkAccessFlags2.ColorAttachmentWrite
            | (isDiscard ? VkAccessFlags2.None : VkAccessFlags2.ColorAttachmentRead);
        VkRenderingAttachmentInfo* attachments = stackalloc VkRenderingAttachmentInfo[Math.Max(colors.Length, 1)];
        VkExtent2D extent = default;
        for (int i = 0; i < colors.Length; i++)
        {
            (VkImageView view, VkExtent2D size) = UseTarget(
                colors[i],
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.ColorAttachmentOutput,
                colorAccess,
                isDiscard);
            extent = i == 0 ? size : extent;
            attachments[i] = new VkRenderingAttachmentInfo
            {
                imageView = view,
                imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = begin.Contents.ToVulkan(),
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue(begin.Clear.R, begin.Clear.G, begin.Clear.B, begin.Clear.A),
            };
        }

        bool hasDepth = begin.Depth.Texture != GpuTexture.None;
        VkRenderingAttachmentInfo depth = default;
        if (hasDepth)
        {
            (VkImageView view, VkExtent2D size) = UseTarget(
                begin.Depth,
                VkImageLayout.DepthAttachmentOptimal,
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                VkAccessFlags2.DepthStencilAttachmentWrite
                    | (isDiscard ? VkAccessFlags2.None : VkAccessFlags2.DepthStencilAttachmentRead),
                isDiscard);
            extent = colors.Length == 0 ? size : extent;

            // Reverse-Z's far is 0, the depth a clear leaves.
            depth = new VkRenderingAttachmentInfo
            {
                imageView = view,
                imageLayout = VkImageLayout.DepthAttachmentOptimal,
                loadOp = begin.Contents.ToVulkan(),
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue(0, 0),
            };
        }

        UseReads(list, index + 1, FindEnd(list, index, GpuCommandType.EndRenderPass), GraphicsShaders);
        _barriers.Record(_device.Api, _commandBuffer);
        VkRenderingInfo info = new()
        {
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            layerCount = 1,
            colorAttachmentCount = (uint)colors.Length,
            pColorAttachments = attachments,
            pDepthAttachment = hasDepth ? &depth : null,
        };
        _device.Api.vkCmdBeginRendering(_commandBuffer, &info);
        Rectangle whole = new(0, 0, (int)extent.width, (int)extent.height);
        SetViewport(whole);
        SetScissor(whole);
    }

    /// <summary>
    /// Waits for what the compute pass reads and writes, in the layouts it uses them in.
    /// </summary>
    private void BeginComputePass(ReadOnlySpan<GpuCommand> list, int index)
    {
        UseReads(
            list,
            index + 1,
            FindEnd(list, index, GpuCommandType.EndComputePass),
            VkPipelineStageFlags2.ComputeShader);
        _barriers.Record(_device.Api, _commandBuffer);
    }

    /// <summary>
    /// Gathers the barriers for what the commands from <paramref name="start"/> to before <paramref name="end"/> use:
    /// what the pass says its shaders, running in <paramref name="stages"/>, read and write, and the index and argument
    /// buffers.
    /// </summary>
    private void UseReads(ReadOnlySpan<GpuCommand> list, int start, int end, VkPipelineStageFlags2 stages)
    {
        for (int i = start; i < end; i++)
        {
            ref readonly GpuCommand command = ref list[i];
            switch (command.Type)
            {
                case GpuCommandType.Reads:
                    foreach (GpuResource resource in _commands.GetResources(command))
                    {
                        UseResource(resource, stages, isWrite: false);
                    }

                    break;
                case GpuCommandType.Writes:
                    foreach (GpuResource resource in _commands.GetResources(command))
                    {
                        UseResource(resource, stages, isWrite: true);
                    }

                    break;
                case GpuCommandType.IndexBuffer:
                    UseBuffer(command.Buffer, VkPipelineStageFlags2.IndexInput, VkAccessFlags2.IndexRead);
                    break;
                case GpuCommandType.DrawIndexedFromBuffer or GpuCommandType.DispatchFromBuffer:
                    UseBuffer(command.Buffer, VkPipelineStageFlags2.DrawIndirect, VkAccessFlags2.IndirectCommandRead);
                    break;
                case GpuCommandType.BeginRenderPass
                    or GpuCommandType.EndRenderPass
                    or GpuCommandType.BeginComputePass
                    or GpuCommandType.EndComputePass
                    or GpuCommandType.Viewport
                    or GpuCommandType.Scissor
                    or GpuCommandType.Pipeline
                    or GpuCommandType.Constants
                    or GpuCommandType.Draw
                    or GpuCommandType.DrawIndexed
                    or GpuCommandType.Dispatch
                    or GpuCommandType.CopyTexture
                    or GpuCommandType.CopyBuffer
                    or GpuCommandType.GenerateMipmaps
                    or GpuCommandType.BeginDebugGroup
                    or GpuCommandType.EndDebugGroup:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(list), command.Type, "There is no such GPU command.");
            }
        }
    }

    private void UseBuffer(GpuBuffer buffer, VkPipelineStageFlags2 stages, VkAccessFlags2 access)
    {
        if (_resources.Buffers[buffer.Id] is { } read) _barriers.Use(ref read.State, stages, access, isWrite: false);
    }

    /// <summary>
    /// A resource a shader reads or writes: a texture read is laid out to be sampled, as the heap's sampled
    /// descriptors say, and one written to be read and written as storage, as its levels' descriptors say.
    /// </summary>
    private void UseResource(GpuResource resource, VkPipelineStageFlags2 stages, bool isWrite)
    {
        const VkAccessFlags2 Storage = VkAccessFlags2.ShaderStorageRead | VkAccessFlags2.ShaderStorageWrite;
        if (resource.Buffer != GpuBuffer.None)
        {
            if (_resources.Buffers[resource.Buffer.Id] is { } buffer)
            {
                _barriers.Use(
                    ref buffer.State,
                    stages,
                    isWrite ? Storage : VkAccessFlags2.ShaderStorageRead,
                    isWrite);
            }

            return;
        }

        if (_resources.Textures[resource.Level.Texture.Id] is not { Window: null } texture) return;

        VkImageLayout layout = isWrite ? VkImageLayout.General : VkImageLayout.ShaderReadOnlyOptimal;
        VkAccessFlags2 access = isWrite ? Storage : VkAccessFlags2.ShaderSampledRead;
        if (resource.IsWhole)
        {
            _barriers.UseWhole(texture, layout, stages, access, isWrite);

            return;
        }

        _barriers.UseOne(
            texture,
            resource.Level.Level,
            texture.GetArrayLayer(resource.Level.Layer),
            layout,
            stages,
            access,
            isWrite);
    }

    /// <summary>
    /// A pass's target, used as <paramref name="layout"/> says, and the view and size it is drawn into at: a window's
    /// acquired image, or a texture's level of a layer or depth slice.
    /// </summary>
    private (VkImageView View, VkExtent2D Size) UseTarget(
        GpuTextureLevel target,
        VkImageLayout layout,
        VkPipelineStageFlags2 stages,
        VkAccessFlags2 access,
        bool isDiscard)
    {
        if (_resources.Textures[target.Texture.Id] is not { } texture) return (VkImageView.Null, default);

        if (texture.Window is { } window)
        {
            int image = window.Image;
            VkImageSubresourceRange color = new(VkImageAspectFlags.Color, 0, 1, 0, 1);
            _barriers.Use(
                window.Images[image],
                color,
                ref window.States[image],
                layout,
                stages,
                access,
                isWrite: true,
                isDiscard);

            return (window.Views[image], window.Extent);
        }

        _barriers.UseOne(
            texture,
            target.Level,
            texture.GetArrayLayer(target.Layer),
            layout,
            stages,
            access,
            isWrite: true,
            isDiscard);
        (uint width, uint height, _) = texture.GetSize(target.Level);

        return (texture.TargetViews[texture.GetTargetIndex(target.Level, target.Layer)], new VkExtent2D(width, height));
    }

    /// <summary>
    /// Sets the viewport over <paramref name="area"/>, flipped: clip space's +Y up, as on the other APIs.
    /// </summary>
    private void SetViewport(Rectangle area)
    {
        VkViewport viewport = new(area.X, area.Y + area.Height, area.Width, -area.Height, 0, 1);
        _device.Api.vkCmdSetViewport(_commandBuffer, 0, viewport);
    }

    /// <summary>
    /// Sets the scissor to <paramref name="area"/>, inside the target's corner, as one before it is an error.
    /// </summary>
    private void SetScissor(Rectangle area)
    {
        int x = Math.Max(area.X, 0);
        int y = Math.Max(area.Y, 0);
        VkRect2D scissor = new(x, y, (uint)Math.Max(area.Right - x, 0), (uint)Math.Max(area.Bottom - y, 0));
        _device.Api.vkCmdSetScissor(_commandBuffer, 0, scissor);
    }

    private void BindPipeline(GpuPipeline pipeline)
    {
        if (_resources.Pipelines[pipeline.Id] is not { } bound) return;

        _pipeline = bound;
        _device.Api.vkCmdBindPipeline(_commandBuffer, bound.BindPoint, bound.Handle);

        // Another bind point's pipeline may have disturbed the heap and the constants since they were last set.
        _heap.Bind(_commandBuffer, bound.BindPoint);
        _isPushStale = true;
    }

    /// <summary>
    /// A constant block, kept to push before the next draw or dispatch.
    /// </summary>
    private void SetConstants(in GpuCommand command)
    {
        ReadOnlySpan<byte> bytes = _commands.GetBytes(command);
        _pushLength = Math.Min(bytes.Length, _push.Length);
        bytes[.._pushLength].CopyTo(_push);
        _isPushStale = true;
    }

    /// <summary>
    /// Pushes the constants if they changed, or a pipeline was bound, since they were last pushed.
    /// </summary>
    private void PushConstants()
    {
        if (_pipeline is not { } pipeline || !_isPushStale || _pushLength == 0) return;

        _isPushStale = false;
        uint size = ((uint)_pushLength + 3) & ~3u;
        fixed (byte* values = _push)
        {
            _device.Api.vkCmdPushConstants(_commandBuffer, pipeline.Layout, VkShaderStageFlags.All, 0, size, values);
        }
    }

    /// <summary>
    /// The source region's size (the whole level where it says 0) from each region's corner.
    /// </summary>
    private void CopyTexture(in GpuCommand command)
    {
        ReadOnlySpan<GpuTextureRegion> run = _commands.GetRegions(command);

        if (_resources.Textures[run[0].Texture.Id] is not { } source
            || _resources.Textures[run[1].Texture.Id] is not { } destination)
        {
            return;
        }

        GpuTextureRegion from = run[0];
        GpuTextureRegion to = run[1];
        uint fromLayer = source.GetArrayLayer(from.Layer);
        uint toLayer = destination.GetArrayLayer(to.Layer);
        _barriers.UseOne(
            source,
            from.Level,
            fromLayer,
            VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags2.Copy,
            VkAccessFlags2.TransferRead,
            isWrite: false);
        _barriers.UseOne(
            destination,
            to.Level,
            toLayer,
            VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags2.Copy,
            VkAccessFlags2.TransferWrite,
            isWrite: true);
        _barriers.Record(_device.Api, _commandBuffer);

        (uint width, uint height, uint depth) = source.GetSize(from.Level);
        VkImageCopy copy = new()
        {
            srcSubresource = new VkImageSubresourceLayers(
                source.Description.Format.ToAspect(),
                from.Level,
                fromLayer,
                1),
            srcOffset = new VkOffset3D((int)from.X, (int)from.Y, (int)from.Z),
            dstSubresource = new VkImageSubresourceLayers(
                destination.Description.Format.ToAspect(),
                to.Level,
                toLayer,
                1),
            dstOffset = new VkOffset3D((int)to.X, (int)to.Y, (int)to.Z),
            extent = new VkExtent3D(
                from.Width != 0 ? from.Width : width,
                from.Height != 0 ? from.Height : height,
                from.Depth != 0 ? from.Depth : depth),
        };
        _device.Api.vkCmdCopyImage(
            _commandBuffer,
            source.Image,
            VkImageLayout.TransferSrcOptimal,
            destination.Image,
            VkImageLayout.TransferDstOptimal,
            1,
            &copy);
    }

    private void CopyBuffer(in GpuCommand command)
    {
        ReadOnlySpan<GpuBuffer> run = _commands.GetBuffers(command);

        if (_resources.Buffers[run[0].Id] is not { } source || _resources.Buffers[run[1].Id] is not { } destination)
            return;

        _barriers.Use(ref source.State, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferRead, isWrite: false);
        _barriers.Use(ref destination.State, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite, isWrite: true);
        _barriers.Record(_device.Api, _commandBuffer);
        VkBufferCopy copy = new() { srcOffset = command.First, dstOffset = command.Offset, size = command.Count };
        _device.Api.vkCmdCopyBuffer(_commandBuffer, source.Handle, destination.Handle, 1, &copy);
    }

    /// <summary>
    /// Each level past the first made from the one before it, every layer, by a linear blit.
    /// </summary>
    private void GenerateMipmaps(VulkanTexture texture)
    {
        VkImageAspectFlags aspect = texture.Description.Format.ToAspect();
        for (uint level = 1; level < texture.Description.Levels; level++)
        {
            for (uint layer = 0; layer < texture.Layers; layer++)
            {
                _barriers.UseOne(
                    texture,
                    level - 1,
                    layer,
                    VkImageLayout.TransferSrcOptimal,
                    VkPipelineStageFlags2.Blit,
                    VkAccessFlags2.TransferRead,
                    isWrite: false);
                _barriers.UseOne(
                    texture,
                    level,
                    layer,
                    VkImageLayout.TransferDstOptimal,
                    VkPipelineStageFlags2.Blit,
                    VkAccessFlags2.TransferWrite,
                    isWrite: true,
                    isDiscard: true);
            }

            _barriers.Record(_device.Api, _commandBuffer);
            (uint fromWidth, uint fromHeight, uint fromDepth) = texture.GetSize(level - 1);
            (uint toWidth, uint toHeight, uint toDepth) = texture.GetSize(level);
            VkImageBlit blit = new()
            {
                srcSubresource = new VkImageSubresourceLayers(aspect, level - 1, 0, texture.Layers),
                dstSubresource = new VkImageSubresourceLayers(aspect, level, 0, texture.Layers),
            };
            blit.srcOffsets[1] = new VkOffset3D((int)fromWidth, (int)fromHeight, (int)fromDepth);
            blit.dstOffsets[1] = new VkOffset3D((int)toWidth, (int)toHeight, (int)toDepth);
            _device.Api.vkCmdBlitImage(
                _commandBuffer,
                texture.Image,
                VkImageLayout.TransferSrcOptimal,
                texture.Image,
                VkImageLayout.TransferDstOptimal,
                1,
                &blit,
                VkFilter.Linear);
        }
    }

    /// <summary>
    /// A label a GPU debugger shows the commands up to the group's end under; a top-level group is timed too.
    /// </summary>
    private void BeginDebugGroup(in GpuCommand command)
    {
        ReadOnlySpan<byte> name = _commands.GetBytes(command);
        if (_depth++ == 0) _isGroupTimed = _isTimed && _timer.Start(_commandBuffer, _timer.GetName(name));

        if (!_device.HasDebugUtils) return;

        byte* terminated = stackalloc byte[name.Length + 1];
        name.CopyTo(new Span<byte>(terminated, name.Length));
        terminated[name.Length] = 0;
        VkDebugUtilsLabelEXT label = new() { pLabelName = terminated };
        _device.InstanceApi.vkCmdBeginDebugUtilsLabelEXT(_commandBuffer, &label);
    }

    private void EndDebugGroup()
    {
        if (_device.HasDebugUtils) _device.InstanceApi.vkCmdEndDebugUtilsLabelEXT(_commandBuffer);

        if (--_depth != 0 || !_isGroupTimed) return;

        _timer.Stop(_commandBuffer);
        _isGroupTimed = false;
    }
}
