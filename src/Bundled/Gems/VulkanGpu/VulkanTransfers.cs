using System.Diagnostics.Metrics;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// Uploads, staged at the call into the next frame's slot's mapped buffer, 16-byte aligned, and copied at the start of
/// that frame's submit, before its commands, in call order; and reads back, which wait for the GPU. A staging buffer a
/// frame outgrows stays until the frame that copies from it is done, and the slot keeps the bigger one.
/// </summary>
internal sealed unsafe class VulkanTransfers : IDisposable
{
    private const ulong InitialBytes = 1024 * 1024;

    private readonly VulkanDevice _device;
    private readonly VulkanResources _resources;
    private readonly Counter<long> _staged;
    private readonly VulkanBuffer?[] _staging;
    private readonly ulong[] _used;
    private readonly List<VulkanBuffer>[] _outgrown;
    private readonly List<Transfer> _transfers = [];

    /// <summary>
    /// Destinations already copied into by the uploads gathered under one barrier: a second copy into one waits for the
    /// first, under a barrier of its own.
    /// </summary>
    private readonly HashSet<object> _copied = [];

    private readonly VkCommandPool _pool;

    public VulkanTransfers(VulkanDevice device, VulkanResources resources, Counter<long> staged, int framesInFlight)
    {
        _device = device;
        _resources = resources;
        _staged = staged;
        _staging = new VulkanBuffer?[framesInFlight];
        _used = new ulong[framesInFlight];
        _outgrown = new List<VulkanBuffer>[framesInFlight];
        for (int slot = 0; slot < framesInFlight; slot++)
        {
            _outgrown[slot] = [];
        }

        VkCommandPoolCreateInfo info = new()
        {
            flags = VkCommandPoolCreateFlags.Transient,
            queueFamilyIndex = device.QueueFamily,
        };
        VkCommandPool pool;
        device.Api.vkCreateCommandPool(&info, &pool);
        _pool = pool;
    }

    internal int Count => _transfers.Count;

    public void Dispose()
    {
        for (int slot = 0; slot < _staging.Length; slot++)
        {
            Reset(slot);
            if (_staging[slot] is { } staging) _resources.Destroy(staging);
        }

        _device.Api.vkDestroyCommandPool(_pool);
    }

    /// <summary>
    /// Takes <paramref name="slot"/>'s staging back once the GPU is done with the frame last copied from it.
    /// </summary>
    internal void Reset(int slot)
    {
        foreach (VulkanBuffer staging in _outgrown[slot])
        {
            _resources.Destroy(staging);
        }

        _outgrown[slot].Clear();
        _used[slot] = 0;
    }

    internal void Upload(int slot, VulkanBuffer buffer, uint offset, ReadOnlySpan<byte> data)
    {
        (VulkanBuffer staging, ulong staged) = Stage(slot, data);
        _transfers.Add(new Transfer(staging, staged, (uint)data.Length, buffer, offset, null, default));
    }

    internal void Upload(int slot, VulkanTexture texture, in GpuTextureRegion region, ReadOnlySpan<byte> data)
    {
        (VulkanBuffer staging, ulong staged) = Stage(slot, data);
        _transfers.Add(new Transfer(staging, staged, (uint)data.Length, null, 0, texture, region));
    }

    /// <summary>
    /// Records every upload queued since the last submit, in order, each after what last used its destination.
    /// </summary>
    internal void Record(VkCommandBuffer commandBuffer, VulkanBarriers barriers)
    {
        int start = 0;
        for (int i = 0; i < _transfers.Count; i++)
        {
            Transfer transfer = _transfers[i];
            object destination = (object?)transfer.Buffer ?? transfer.Texture ?? throw new InvalidOperationException();
            if (!_copied.Add(destination))
            {
                RecordCopies(commandBuffer, barriers, start, i);
                start = i;
                _copied.Clear();
                _copied.Add(destination);
            }

            if (transfer.Buffer is { } buffer)
            {
                barriers.Use(ref buffer.State, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite, isWrite: true);
                continue;
            }

            if (transfer.Texture is not { } texture) continue;

            barriers.UseOne(
                texture,
                transfer.Region.Level,
                texture.GetArrayLayer(transfer.Region.Layer),
                VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags2.Copy,
                VkAccessFlags2.TransferWrite,
                isWrite: true);
        }

        RecordCopies(commandBuffer, barriers, start, _transfers.Count);
        _transfers.Clear();
        _copied.Clear();
    }

    /// <summary>
    /// The first <paramref name="bytes"/> of a buffer, after everything submitted so far: waits for the GPU.
    /// </summary>
    internal Result<byte[]> Read(VulkanBuffer buffer, uint bytes, VulkanBarriers barriers)
    {
        VulkanBuffer read = _resources.CreateBuffer(bytes, VkBufferUsageFlags.TransferDst, isHost: true);
        try
        {
            VkCommandBuffer commandBuffer = Begin();
            barriers.Use(ref buffer.State, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferRead, isWrite: false);
            barriers.Record(_device.Api, commandBuffer);
            VkBufferCopy copy = new() { size = bytes };
            _device.Api.vkCmdCopyBuffer(commandBuffer, buffer.Handle, read.Handle, 1, &copy);
            return Finish(commandBuffer, read, bytes);
        }
        finally
        {
            _resources.Destroy(read);
        }
    }

    /// <summary>
    /// Level 0, layer 0 of a texture, tightly packed rows top to bottom, after everything submitted so far: waits for the
    /// GPU.
    /// </summary>
    internal Result<byte[]> Read(VulkanTexture texture, VulkanBarriers barriers)
    {
        GpuTextureDescription description = texture.Description;
        (uint rowBytes, uint rows) = GetPacking(description.Format, description.Width, description.Height);
        uint bytes = rowBytes * rows;
        VulkanBuffer read = _resources.CreateBuffer(bytes, VkBufferUsageFlags.TransferDst, isHost: true);
        try
        {
            VkCommandBuffer commandBuffer = Begin();
            barriers.UseOne(
                texture,
                0,
                0,
                VkImageLayout.TransferSrcOptimal,
                VkPipelineStageFlags2.Copy,
                VkAccessFlags2.TransferRead,
                isWrite: false);
            barriers.Record(_device.Api, commandBuffer);
            VkBufferImageCopy copy = new()
            {
                imageSubresource = new VkImageSubresourceLayers(description.Format.ToAspect(), 0, 0, 1),
                imageExtent = new VkExtent3D(description.Width, description.Height, 1),
            };
            _device.Api.vkCmdCopyImageToBuffer(
                commandBuffer,
                texture.Image,
                VkImageLayout.TransferSrcOptimal,
                read.Handle,
                1,
                &copy);
            return Finish(commandBuffer, read, bytes);
        }
        finally
        {
            _resources.Destroy(read);
        }
    }

    /// <summary>
    /// Bytes a row of <paramref name="width"/> texels takes, tightly packed, and the rows of <paramref name="height"/>
    /// there are: a compressed format's are rows of 4 x 4 blocks.
    /// </summary>
    private static (uint RowBytes, uint Rows) GetPacking(GpuFormat format, uint width, uint height) => format switch
    {
        GpuFormat.Bc7Unorm or GpuFormat.Bc7Srgb => ((width + 3) / 4 * 16, (height + 3) / 4),
        GpuFormat.R8Unorm => (width, height),
        GpuFormat.Rg8Unorm or GpuFormat.R16Float => (width * 2, height),
        GpuFormat.Rgba16Float or GpuFormat.Rg32Float => (width * 8, height),
        GpuFormat.Rgba32Float => (width * 16, height),
        GpuFormat.Invalid => (0, 0),
        GpuFormat.Rgba8Unorm
            or GpuFormat.Rgba8Srgb
            or GpuFormat.Bgra8Unorm
            or GpuFormat.Bgra8Srgb
            or GpuFormat.Rgb10A2Unorm
            or GpuFormat.Rg16Float
            or GpuFormat.R32Float
            or GpuFormat.R11G11B10Float
            or GpuFormat.R32Uint
            or GpuFormat.D32Float => (width * 4, height),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such GPU format."),
    };

    /// <summary>
    /// Copies <paramref name="data"/> into the slot's staging; returns the buffer and offset the copy reads it from.
    /// </summary>
    private (VulkanBuffer Staging, ulong Offset) Stage(int slot, ReadOnlySpan<byte> data)
    {
        _staged.Add(data.Length);
        ulong offset = (_used[slot] + 15) & ~15ul;
        if (_staging[slot] is not { } staging || offset + (ulong)data.Length > staging.Size)
        {
            ulong size = Math.Max(InitialBytes, (_staging[slot]?.Size ?? 0) * 2);
            while (size < (ulong)data.Length)
            {
                size *= 2;
            }

            if (_staging[slot] is { } old) _outgrown[slot].Add(old);

            staging = _resources.CreateBuffer(size, VkBufferUsageFlags.TransferSrc, isHost: true);
            _staging[slot] = staging;
            offset = 0;
        }

        data.CopyTo(new Span<byte>(staging.Mapped + offset, data.Length));
        _used[slot] = offset + (ulong)data.Length;
        return (staging, offset);
    }

    /// <summary>
    /// The barrier for the uploads from <paramref name="start"/> to before <paramref name="end"/>, then their copies.
    /// </summary>
    private void RecordCopies(VkCommandBuffer commandBuffer, VulkanBarriers barriers, int start, int end)
    {
        barriers.Record(_device.Api, commandBuffer);
        for (int i = start; i < end; i++)
        {
            Transfer transfer = _transfers[i];
            if (transfer.Buffer is { } buffer)
            {
                VkBufferCopy copy = new()
                {
                    srcOffset = transfer.Staged,
                    dstOffset = transfer.Offset,
                    size = transfer.Bytes,
                };
                _device.Api.vkCmdCopyBuffer(commandBuffer, transfer.Staging.Handle, buffer.Handle, 1, &copy);
                continue;
            }

            if (transfer.Texture is not { } texture) continue;

            GpuTextureRegion region = transfer.Region;
            (uint width, uint height, uint depth) = texture.GetSize(region.Level);
            VkBufferImageCopy texels = new()
            {
                bufferOffset = transfer.Staged,
                imageSubresource = new VkImageSubresourceLayers(
                    texture.Description.Format.ToAspect(),
                    region.Level,
                    texture.GetArrayLayer(region.Layer),
                    1),
                imageOffset = new VkOffset3D((int)region.X, (int)region.Y, (int)region.Z),
                imageExtent = new VkExtent3D(
                    region.Width != 0 ? region.Width : width,
                    region.Height != 0 ? region.Height : height,
                    region.Depth != 0 ? region.Depth : depth),
            };
            _device.Api.vkCmdCopyBufferToImage(
                commandBuffer,
                transfer.Staging.Handle,
                texture.Image,
                VkImageLayout.TransferDstOptimal,
                1,
                &texels);
        }
    }

    private VkCommandBuffer Begin()
    {
        VkCommandBufferAllocateInfo info = new()
        {
            commandPool = _pool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = 1,
        };
        VkCommandBuffer commandBuffer;
        _device.Api.vkAllocateCommandBuffers(&info, &commandBuffer);
        _device.Api.vkBeginCommandBuffer(commandBuffer, VkCommandBufferUsageFlags.OneTimeSubmit);
        return commandBuffer;
    }

    /// <summary>
    /// Submits a read, waits for the GPU, and copies the bytes out.
    /// </summary>
    private Result<byte[]> Finish(VkCommandBuffer commandBuffer, VulkanBuffer read, uint bytes)
    {
        _device.Api.vkEndCommandBuffer(commandBuffer);
        VkCommandBufferSubmitInfo buffer = new() { commandBuffer = commandBuffer };
        VkSubmitInfo2 submit = new() { commandBufferInfoCount = 1, pCommandBufferInfos = &buffer };
        VkResult result = _device.Api.vkQueueSubmit2(_device.Queue, 1, &submit, VkFence.Null);
        if (result == VkResult.Success) result = _device.Api.vkQueueWaitIdle(_device.Queue);

        _device.Api.vkFreeCommandBuffers(_pool, 1, &commandBuffer);
        return result == VkResult.Success
            ? Result.Success(new ReadOnlySpan<byte>(read.Mapped, (int)bytes).ToArray())
            : Result.Failure<byte[]>($"The read never arrived: {result}.");
    }

    /// <summary>
    /// One queued upload: staged bytes into a buffer at <see cref="Offset"/>, or into a texture's region.
    /// </summary>
    private readonly record struct Transfer(
        VulkanBuffer Staging,
        ulong Staged,
        uint Bytes,
        VulkanBuffer? Buffer,
        uint Offset,
        VulkanTexture? Texture,
        GpuTextureRegion Region);
}
