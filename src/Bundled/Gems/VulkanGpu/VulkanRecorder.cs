using Magic.Contexts.Gpu;
using Vortice.Vulkan;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.VulkanGpu;

/// <summary>
/// Turns a frame's commands, the plain data <see cref="GpuCommands"/> holds, into a Vulkan command buffer, running them
/// as the renderer and its shaders are written for:
/// <list type="bullet">
/// <item>A pass ends where everything it wrote is done before anything after it uses it. Before a pass begins, every
/// resource its commands touch (its targets, what it binds to read, what a compute pass says it writes, its vertex,
/// index and argument buffers) is waited for and put in the layout the pass uses, in one barrier.</item>
/// <item>A stage's bindings and constants stay until bound again; each draw or dispatch writes the descriptor sets it
/// needs from them into sets of this frame's, as the pipeline's shapes say, and names its constants' offsets.</item>
/// <item>The viewport is flipped, so clip space is +Y up, as the shaders are written for, and a pass starts with the
/// viewport and the scissor over its whole target.</item>
/// <item>A pass into a window draws into the image the window acquired this frame, acquired the first time one does;
/// a window with no image, minimised, has its passes skipped.</item>
/// </list>
/// </summary>
internal sealed unsafe class VulkanRecorder(
    VulkanDevice device,
    VulkanResources resources,
    VulkanPipelines pipelines,
    VulkanDescriptors descriptors,
    VulkanWindows windows,
    VulkanBarriers barriers,
    VulkanTimer timer)
{
    private const int MaxSampled = 16;
    private const int MaxReads = 8;
    private const uint ArgumentsStride = 20;

    private readonly VulkanDevice _device = device;
    private readonly VulkanResources _resources = resources;
    private readonly VulkanPipelines _pipelines = pipelines;
    private readonly VulkanDescriptors _descriptors = descriptors;
    private readonly VulkanWindows _windows = windows;
    private readonly VulkanBarriers _barriers = barriers;
    private readonly VulkanTimer _timer = timer;

    /// <summary>
    /// What each stage (by <see cref="GpuStage"/>) has bound to read, and the offset of its constants this frame.
    /// </summary>
    private readonly GpuSampledTexture[][] _sampled =
        [new GpuSampledTexture[MaxSampled], new GpuSampledTexture[MaxSampled], new GpuSampledTexture[MaxSampled]];
    private readonly GpuTexture[][] _textures =
        [new GpuTexture[MaxReads], new GpuTexture[MaxReads], new GpuTexture[MaxReads]];
    private readonly GpuBuffer[][] _buffers =
        [new GpuBuffer[MaxReads], new GpuBuffer[MaxReads], new GpuBuffer[MaxReads]];
    private readonly uint[] _constants = new uint[3];

    /// <summary>
    /// What the compute pass under way writes, in the order its shader numbers them.
    /// </summary>
    private readonly List<GpuTextureLevel> _writtenTextures = [];
    private readonly List<GpuBuffer> _writtenBuffers = [];

    /// <summary>
    /// The pipeline's descriptor sets, by set number, that must be written again before the next draw or dispatch.
    /// </summary>
    private readonly bool[] _isSetStale = new bool[4];

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
    /// Records <paramref name="commands"/> into <paramref name="commandBuffer"/>, which frame slot
    /// <paramref name="slot"/>'s descriptors and constants serve; each top-level debug group timed when
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
        Array.Fill(_constants, 0u);
        ReadOnlySpan<GpuCommand> list = commands.Commands;
        for (int i = 0; i < list.Length; i++)
        {
            if (_isSkipping && list[i].Type != GpuCommandType.EndRenderPass) continue;

            Run(list, i);
        }
    }

    private static VkPipelineStageFlags2 ToStages(GpuStage stage) => stage switch
    {
        GpuStage.Vertex => VkPipelineStageFlags2.VertexShader,
        GpuStage.Fragment => VkPipelineStageFlags2.FragmentShader,
        GpuStage.Compute => VkPipelineStageFlags2.ComputeShader,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "There is no such shader stage."),
    };

    private static int FindEnd(ReadOnlySpan<GpuCommand> list, int begin, GpuCommandType end)
    {
        for (int i = begin + 1; i < list.Length; i++)
        {
            if (list[i].Type == end) return i;
        }

        return list.Length;
    }

    private static VkWriteDescriptorSet MakeWrite(
        VkDescriptorSet set,
        uint binding,
        SetShape shape,
        VkDescriptorImageInfo* pImageInfo = null,
        VkDescriptorBufferInfo* pBufferInfo = null) => new()
        {
            dstSet = set,
            dstBinding = binding,
            descriptorCount = 1,
            descriptorType = VulkanPipelines.GetDescriptorType(shape, binding),
            pImageInfo = pImageInfo,
            pBufferInfo = pBufferInfo,
        };

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
                _writtenTextures.Clear();
                _writtenBuffers.Clear();
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
            case GpuCommandType.VertexBuffers:
                BindVertexBuffers(command);
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
            case GpuCommandType.Buffers:
                _commands.GetBuffers(command).CopyTo(_buffers[(int)command.Stage]);
                MarkStale(command.Stage);
                break;
            case GpuCommandType.RawTextures:
                _commands.GetTextures(command).CopyTo(_textures[(int)command.Stage]);
                MarkStale(command.Stage);
                break;
            case GpuCommandType.Textures:
                _commands.GetSampled(command).CopyTo(_sampled[(int)command.Stage]);
                MarkStale(command.Stage);
                break;
            case GpuCommandType.Constants:
                _constants[(int)command.Stage] = _descriptors.Push(_slot, _commands.GetBytes(command));
                break;
            case GpuCommandType.Draw:
                BindSets();
                _device.Api.vkCmdDraw(
                    _commandBuffer,
                    command.Count,
                    command.Instances,
                    command.First,
                    command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexed:
                BindSets();
                _device.Api.vkCmdDrawIndexed(
                    _commandBuffer,
                    command.Count,
                    command.Instances,
                    command.First,
                    command.VertexOffset,
                    command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexedFromBuffer:
                BindSets();
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
                BindSets();
                _device.Api.vkCmdDispatch(_commandBuffer, command.GroupsX, command.GroupsY, command.GroupsZ);
                break;
            case GpuCommandType.DispatchFromBuffer:
                BindSets();
                if (_resources.Buffers[command.Buffer.Id] is { } groups)
                {
                    _device.Api.vkCmdDispatchIndirect(_commandBuffer, groups.Handle, command.Offset);
                }

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

        UseReads(list, index + 1, FindEnd(list, index, GpuCommandType.EndRenderPass));
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
    /// Waits for what the compute pass writes and reads, in the layouts it uses them in.
    /// </summary>
    private void BeginComputePass(ReadOnlySpan<GpuCommand> list, int index)
    {
        ref readonly GpuCommand begin = ref list[index];
        const VkAccessFlags2 Writes = VkAccessFlags2.ShaderStorageRead | VkAccessFlags2.ShaderStorageWrite;
        foreach (GpuTextureLevel level in _commands.GetLevels(begin))
        {
            _writtenTextures.Add(level);
            if (_resources.Textures[level.Texture.Id] is not { } texture) continue;

            _barriers.UseOne(
                texture,
                level.Level,
                texture.GetArrayLayer(level.Layer),
                VkImageLayout.General,
                VkPipelineStageFlags2.ComputeShader,
                Writes,
                isWrite: true);
        }

        foreach (GpuBuffer written in _commands.GetBuffers(begin))
        {
            _writtenBuffers.Add(written);
            if (_resources.Buffers[written.Id] is { } buffer)
            {
                _barriers.Use(ref buffer.State, VkPipelineStageFlags2.ComputeShader, Writes, isWrite: true);
            }
        }

        UseReads(list, index + 1, FindEnd(list, index, GpuCommandType.EndComputePass));
        _barriers.Record(_device.Api, _commandBuffer);
        _isSetStale[1] = true;
    }

    /// <summary>
    /// Gathers the barriers for what the commands from <paramref name="start"/> to before <paramref name="end"/> read:
    /// the textures and buffers each stage binds, and the vertex, index and argument buffers.
    /// </summary>
    private void UseReads(ReadOnlySpan<GpuCommand> list, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            ref readonly GpuCommand command = ref list[i];
            switch (command.Type)
            {
                case GpuCommandType.Textures:
                    foreach (GpuSampledTexture sampled in _commands.GetSampled(command))
                    {
                        UseSampled(sampled.Texture, command.Stage);
                    }

                    break;
                case GpuCommandType.RawTextures:
                    foreach (GpuTexture texture in _commands.GetTextures(command))
                    {
                        UseSampled(texture, command.Stage);
                    }

                    break;
                case GpuCommandType.Buffers:
                    foreach (GpuBuffer buffer in _commands.GetBuffers(command))
                    {
                        UseBuffer(buffer, ToStages(command.Stage), VkAccessFlags2.ShaderStorageRead);
                    }

                    break;
                case GpuCommandType.VertexBuffers:
                    foreach (GpuBuffer buffer in _commands.GetBuffers(command))
                    {
                        UseBuffer(
                            buffer,
                            VkPipelineStageFlags2.VertexAttributeInput,
                            VkAccessFlags2.VertexAttributeRead);
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

    /// <summary>
    /// A texture a stage reads whole, filtered or texel by texel: a shader declares both as sampled images.
    /// </summary>
    private void UseSampled(GpuTexture texture, GpuStage stage)
    {
        if (_resources.Textures[texture.Id] is not { Window: null } read) return;

        _barriers.UseWhole(
            read,
            VkImageLayout.ShaderReadOnlyOptimal,
            ToStages(stage),
            VkAccessFlags2.ShaderSampledRead,
            isWrite: false);
    }

    private void UseBuffer(GpuBuffer buffer, VkPipelineStageFlags2 stages, VkAccessFlags2 access)
    {
        if (_resources.Buffers[buffer.Id] is { } read) _barriers.Use(ref read.State, stages, access, isWrite: false);
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
        Array.Fill(_isSetStale, true);
    }

    private void BindVertexBuffers(in GpuCommand command)
    {
        ReadOnlySpan<GpuBuffer> run = _commands.GetBuffers(command);
        VkBuffer* buffers = stackalloc VkBuffer[Math.Max(run.Length, 1)];
        ulong* offsets = stackalloc ulong[Math.Max(run.Length, 1)];
        for (int i = 0; i < run.Length; i++)
        {
            buffers[i] = _resources.Buffers[run[i].Id]?.Handle ?? VkBuffer.Null;
            offsets[i] = 0;
        }

        _device.Api.vkCmdBindVertexBuffers(_commandBuffer, 0, (uint)run.Length, buffers, offsets);
    }

    /// <summary>
    /// A stage's bindings changed: the read set it is written into must be written again.
    /// </summary>
    private void MarkStale(GpuStage stage)
    {
        if (stage == GpuStage.Fragment) _isSetStale[2] = true;
        else _isSetStale[0] = true;
    }

    /// <summary>
    /// Writes into sets of this frame's whatever the pipeline reads that changed since the last draw or dispatch, and
    /// binds them, with every stage's constants at their offsets.
    /// </summary>
    private void BindSets()
    {
        if (_pipeline is not { } pipeline) return;

        VkDescriptorSet uniforms = VkDescriptorSet.Null;
        for (int set = 0; set < pipeline.Sets.Length; set++)
        {
            SetShape shape = pipeline.Sets[set];
            if (shape.IsEmpty) continue;

            GpuStage stage = (pipeline.BindPoint, set) switch
            {
                (VkPipelineBindPoint.Compute, _) => GpuStage.Compute,
                (_, < 2) => GpuStage.Vertex,
                _ => GpuStage.Fragment,
            };
            if (shape.Kind == SetKind.Uniform)
            {
                if (uniforms.IsNull) uniforms = _descriptors.GetUniformSet(_slot);

                uint offset = _constants[(int)stage];
                _device.Api.vkCmdBindDescriptorSets(
                    _commandBuffer,
                    pipeline.BindPoint,
                    pipeline.Layout,
                    (uint)set,
                    1,
                    &uniforms,
                    1,
                    &offset);
                continue;
            }

            if (!_isSetStale[set]) continue;

            _isSetStale[set] = false;
            VkDescriptorSet written = _descriptors.Allocate(_slot, _pipelines.GetSetLayout(shape));
            if (shape.Kind == SetKind.Write) WriteWrites(written, shape);
            else WriteReads(written, shape, stage);

            _device.Api.vkCmdBindDescriptorSets(
                _commandBuffer,
                pipeline.BindPoint,
                pipeline.Layout,
                (uint)set,
                1,
                &written);
        }
    }

    /// <summary>
    /// A read set from what <paramref name="stage"/> bound: its sampled textures with their samplers, the textures it
    /// reads texel by texel, then its storage buffers, each from register 0.
    /// </summary>
    private void WriteReads(VkDescriptorSet set, SetShape shape, GpuStage stage)
    {
        uint count = shape.Sampled + shape.Textures + shape.Buffers;
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[(int)count];
        VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[(int)Math.Max(
            shape.Sampled + shape.Textures,
            1)];
        VkDescriptorBufferInfo* buffers = stackalloc VkDescriptorBufferInfo[(int)Math.Max(shape.Buffers, 1)];
        uint written = 0;
        for (uint binding = 0; binding < shape.Sampled + shape.Textures; binding++)
        {
            bool isSampled = binding < shape.Sampled;
            GpuTexture id = isSampled
                ? _sampled[(int)stage][binding].Texture
                : _textures[(int)stage][binding - shape.Sampled];
            if (_resources.Textures[id.Id] is not { Window: null } texture) continue;

            images[binding] = new VkDescriptorImageInfo
            {
                sampler = isSampled ? _resources.Samplers[_sampled[(int)stage][binding].Sampler.Id] : VkSampler.Null,
                imageView = texture.SampledView,
                imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            };
            writes[written++] = MakeWrite(set, binding, shape, pImageInfo: &images[binding]);
        }

        for (uint i = 0; i < shape.Buffers; i++)
        {
            if (_resources.Buffers[_buffers[(int)stage][i].Id] is not { } buffer) continue;

            buffers[i] = new VkDescriptorBufferInfo
            {
                buffer = buffer.Handle,
                offset = 0,
                range = Vulkan.VK_WHOLE_SIZE
            };
            writes[written++] = MakeWrite(set, shape.Sampled + shape.Textures + i, shape, pBufferInfo: &buffers[i]);
        }

        _device.Api.vkUpdateDescriptorSets(written, writes, 0, null);
    }

    /// <summary>
    /// The compute pass's write set: the levels it said it writes, then its buffers, in that order.
    /// </summary>
    private void WriteWrites(VkDescriptorSet set, SetShape shape)
    {
        uint count = shape.Textures + shape.Buffers;
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[(int)count];
        VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[(int)Math.Max(shape.Textures, 1)];
        VkDescriptorBufferInfo* buffers = stackalloc VkDescriptorBufferInfo[(int)Math.Max(shape.Buffers, 1)];
        uint written = 0;
        for (int i = 0; i < shape.Textures && i < _writtenTextures.Count; i++)
        {
            GpuTextureLevel level = _writtenTextures[i];
            if (_resources.Textures[level.Texture.Id] is not { } texture || texture.StorageViews.Length == 0) continue;

            uint layer = texture.GetArrayLayer(level.Layer);
            images[i] = new VkDescriptorImageInfo
            {
                imageView = texture.StorageViews[(layer * texture.Description.Levels) + level.Level],
                imageLayout = VkImageLayout.General,
            };
            writes[written++] = MakeWrite(set, (uint)i, shape, pImageInfo: &images[i]);
        }

        for (int i = 0; i < shape.Buffers && i < _writtenBuffers.Count; i++)
        {
            if (_resources.Buffers[_writtenBuffers[i].Id] is not { } buffer) continue;

            buffers[i] = new VkDescriptorBufferInfo
            {
                buffer = buffer.Handle,
                offset = 0,
                range = Vulkan.VK_WHOLE_SIZE
            };
            writes[written++] = MakeWrite(set, shape.Textures + (uint)i, shape, pBufferInfo: &buffers[i]);
        }

        _device.Api.vkUpdateDescriptorSets(written, writes, 0, null);
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
        {
            return;
        }

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
