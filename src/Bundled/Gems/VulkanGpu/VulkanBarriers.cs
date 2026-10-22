using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// The barriers before a pass, gathered from each use of a buffer or a texture's level the pass makes, and recorded
/// as one: a memory barrier for what only has to wait for an earlier access and see its writes, and an image barrier for
/// each level of a layer whose layout changes. Every access since the last write is waited for before a write; a read
/// waits for the last write only in a stage it was not yet made visible to, so reads that follow reads cost nothing.
/// The state each use leaves is the resource's own, carried from pass to pass and frame to frame, as every frame goes
/// to one queue in order.
/// </summary>
internal sealed unsafe class VulkanBarriers
{
    private const VkAccessFlags2 Writes = VkAccessFlags2.ShaderWrite
        | VkAccessFlags2.ShaderStorageWrite
        | VkAccessFlags2.ColorAttachmentWrite
        | VkAccessFlags2.DepthStencilAttachmentWrite
        | VkAccessFlags2.TransferWrite
        | VkAccessFlags2.HostWrite
        | VkAccessFlags2.MemoryWrite;

    private readonly List<VkImageMemoryBarrier2> _images = [];
    private VkMemoryBarrier2 _memory;

    /// <summary>
    /// A buffer read in <paramref name="stages"/> as <paramref name="access"/> says, or written when
    /// <paramref name="isWrite"/>.
    /// </summary>
    internal void Use(ref ResourceState state, VkPipelineStageFlags2 stages, VkAccessFlags2 access, bool isWrite)
    {
        if (isWrite)
        {
            WaitForAll(ref state, stages, access);
            state = new ResourceState
            {
                Layout = state.Layout,
                WriteStages = stages,
                WriteAccess = access & Writes,
            };

            return;
        }

        if (state.WriteStages != VkPipelineStageFlags2.None
            && (stages & ~state.VisibleStages) != VkPipelineStageFlags2.None)
        {
            AddMemory(state.WriteStages, state.WriteAccess, stages, access);
            state.VisibleStages |= stages;
        }

        state.ReadStages |= stages;
    }

    /// <summary>
    /// Levels and layers of <paramref name="image"/> that share one state used in <paramref name="layout"/>, read or
    /// written as a buffer is (<see cref="Use(ref ResourceState, VkPipelineStageFlags2, VkAccessFlags2, bool)"/>); what
    /// they held is thrown away on the way when <paramref name="isDiscard"/>, as a pass that clears them or covers them
    /// whole needs none of it.
    /// </summary>
    internal void Use(
        VkImage image,
        in VkImageSubresourceRange range,
        ref ResourceState state,
        VkImageLayout layout,
        VkPipelineStageFlags2 stages,
        VkAccessFlags2 access,
        bool isWrite,
        bool isDiscard = false)
    {
        if (state.Layout == layout && !isDiscard)
        {
            Use(ref state, stages, access, isWrite);

            return;
        }

        _images.Add(new VkImageMemoryBarrier2
        {
            srcStageMask = state.WriteStages | state.ReadStages,
            srcAccessMask = state.WriteAccess,
            dstStageMask = stages,
            dstAccessMask = access,
            oldLayout = isDiscard ? VkImageLayout.Undefined : state.Layout,
            newLayout = layout,
            srcQueueFamilyIndex = Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vulkan.VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = range,
        });

        // The layout changed in this barrier, ordered before every access of the pass in these stages, as a write is.
        state = new ResourceState
        {
            Layout = layout,
            WriteStages = stages,
            WriteAccess = isWrite ? access & Writes : VkAccessFlags2.None,
            ReadStages = isWrite ? VkPipelineStageFlags2.None : stages,
            VisibleStages = stages,
        };
    }

    /// <summary>
    /// Every level of every layer of <paramref name="texture"/> used alike, as a texture sampled whole is: one barrier
    /// for the image while they share a state, one for each that differs otherwise, after which they share one again.
    /// </summary>
    internal void UseWhole(
        VulkanTexture texture,
        VkImageLayout layout,
        VkPipelineStageFlags2 stages,
        VkAccessFlags2 access,
        bool isWrite)
    {
        VkImageAspectFlags aspect = texture.Description.Format.ToAspect();
        if (texture.IsWhole)
        {
            VkImageSubresourceRange all = new(aspect, 0, texture.Description.Levels, 0, texture.Layers);
            Use(texture.Image, all, ref texture.Whole, layout, stages, access, isWrite);

            return;
        }

        ResourceState merged = new() { Layout = layout, VisibleStages = VkPipelineStageFlags2.AllCommands };
        for (uint layer = 0; layer < texture.Layers; layer++)
        {
            for (uint level = 0; level < texture.Description.Levels; level++)
            {
                ref ResourceState state = ref texture.GetState(level, layer);
                Use(
                    texture.Image,
                    new VkImageSubresourceRange(aspect, level, 1, layer, 1),
                    ref state,
                    layout,
                    stages,
                    access,
                    isWrite);
                merged.WriteStages |= state.WriteStages;
                merged.WriteAccess |= state.WriteAccess;
                merged.ReadStages |= state.ReadStages;
                merged.VisibleStages &= state.VisibleStages;
            }
        }

        texture.Whole = merged;
        texture.IsWhole = true;
    }

    /// <summary>
    /// One level of one layer of <paramref name="texture"/> used on its own, as a render target or a copy is: from now
    /// on its levels and layers each have a state, until the texture is used whole again.
    /// </summary>
    internal void UseOne(
        VulkanTexture texture,
        uint level,
        uint layer,
        VkImageLayout layout,
        VkPipelineStageFlags2 stages,
        VkAccessFlags2 access,
        bool isWrite,
        bool isDiscard = false)
    {
        if (texture.IsWhole)
        {
            Array.Fill(texture.States, texture.Whole);
            texture.IsWhole = false;
        }

        VkImageSubresourceRange one = new(texture.Description.Format.ToAspect(), level, 1, layer, 1);
        Use(texture.Image, one, ref texture.GetState(level, layer), layout, stages, access, isWrite, isDiscard);
    }

    /// <summary>
    /// Records what was gathered as one barrier, nothing when there is none, and starts gathering again.
    /// </summary>
    internal void Record(VkDeviceApi api, VkCommandBuffer commandBuffer)
    {
        bool hasMemory = _memory.dstStageMask != VkPipelineStageFlags2.None;

        if (!hasMemory && _images.Count == 0) return;

        VkMemoryBarrier2 memory = _memory;
        memory.sType = VkStructureType.MemoryBarrier2;
        fixed (VkImageMemoryBarrier2* images = CollectionsMarshal.AsSpan(_images))
        {
            VkDependencyInfo dependency = new()
            {
                memoryBarrierCount = hasMemory ? 1u : 0u,
                pMemoryBarriers = &memory,
                imageMemoryBarrierCount = (uint)_images.Count,
                pImageMemoryBarriers = images,
            };
            api.vkCmdPipelineBarrier2(commandBuffer, &dependency);
        }

        _images.Clear();
        _memory = default;
    }

    /// <summary>
    /// Before a write: every access since the last write, and that write, finished, and the write's results seen.
    /// </summary>
    private void WaitForAll(ref ResourceState state, VkPipelineStageFlags2 stages, VkAccessFlags2 access)
    {
        VkPipelineStageFlags2 before = state.WriteStages | state.ReadStages;
        if (before != VkPipelineStageFlags2.None) AddMemory(before, state.WriteAccess, stages, access);
    }

    private void AddMemory(
        VkPipelineStageFlags2 sourceStages,
        VkAccessFlags2 sourceAccess,
        VkPipelineStageFlags2 stages,
        VkAccessFlags2 access)
    {
        _memory.srcStageMask |= sourceStages;
        _memory.srcAccessMask |= sourceAccess;
        _memory.dstStageMask |= stages;
        _memory.dstAccessMask |= access;
    }
}
