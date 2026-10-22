using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// Each frame slot's descriptor sets and constants, made again when the slot comes round: sets come from pools that
/// are reset whole rather than freed one by one, more pools made as a frame needs them; constants are written into a
/// mapped ring, one stage's block at a time, and read through one dynamic uniform buffer whose offset each draw names,
/// A ring a frame outgrows stays until the frames that read it are done.
/// </summary>
internal sealed unsafe class VulkanDescriptors : IDisposable
{
    private const uint SetsPerPool = 1024;
    private const ulong InitialRingBytes = 1024 * 1024;

    private readonly VulkanDevice _device;
    private readonly VulkanResources _resources;
    private readonly VulkanPipelines _pipelines;
    private readonly List<VkDescriptorPool>[] _pools;
    private readonly int[] _poolInUse;
    private readonly VulkanBuffer?[] _rings;
    private readonly ulong[] _ringUsed;
    private readonly VkDescriptorSet[] _uniformSets;
    private readonly List<VulkanBuffer>[] _outgrown;

    /// <summary>
    /// Bytes every constants descriptor can read from its offset, so every ring keeps that much past its last block;
    /// and the step offsets come in.
    /// </summary>
    private readonly ulong _range;
    private readonly ulong _alignment;

    public VulkanDescriptors(
        VulkanDevice device,
        VulkanResources resources,
        VulkanPipelines pipelines,
        int framesInFlight)
    {
        _device = device;
        _resources = resources;
        _pipelines = pipelines;
        _pools = new List<VkDescriptorPool>[framesInFlight];
        _poolInUse = new int[framesInFlight];
        _rings = new VulkanBuffer?[framesInFlight];
        _ringUsed = new ulong[framesInFlight];
        _uniformSets = new VkDescriptorSet[framesInFlight];
        _outgrown = new List<VulkanBuffer>[framesInFlight];
        for (int slot = 0; slot < framesInFlight; slot++)
        {
            _pools[slot] = [];
            _outgrown[slot] = [];
        }

        _range = Math.Min(device.Limits.maxUniformBufferRange, 64u * 1024);
        _alignment = Math.Max(device.Limits.minUniformBufferOffsetAlignment, 16);
    }

    public void Dispose()
    {
        for (int slot = 0; slot < _pools.Length; slot++)
        {
            Reset(slot);
            foreach (VkDescriptorPool pool in _pools[slot])
            {
                _device.Api.vkDestroyDescriptorPool(pool);
            }

            if (_rings[slot] is { } ring) _resources.Destroy(ring);
        }
    }

    /// <summary>
    /// Takes <paramref name="slot"/> back once the GPU is done with the frame last drawn from it: every set it handed out
    /// is free again, and so is its ring.
    /// </summary>
    internal void Reset(int slot)
    {
        foreach (VkDescriptorPool pool in _pools[slot])
        {
            _device.Api.vkResetDescriptorPool(pool, VkDescriptorPoolResetFlags.None);
        }

        foreach (VulkanBuffer ring in _outgrown[slot])
        {
            _resources.Destroy(ring);
        }

        _outgrown[slot].Clear();
        _poolInUse[slot] = 0;
        _ringUsed[slot] = 0;
        _uniformSets[slot] = VkDescriptorSet.Null;
    }

    /// <summary>
    /// A set of <paramref name="layout"/> for this frame, from the slot's pools, another made when they are full.
    /// </summary>
    internal VkDescriptorSet Allocate(int slot, VkDescriptorSetLayout layout)
    {
        List<VkDescriptorPool> pools = _pools[slot];
        while (true)
        {
            if (_poolInUse[slot] == pools.Count) pools.Add(CreatePool());

            VkDescriptorSetAllocateInfo info = new()
            {
                descriptorPool = pools[_poolInUse[slot]],
                descriptorSetCount = 1,
                pSetLayouts = &layout,
            };
            VkDescriptorSet set;
            VkResult result = _device.Api.vkAllocateDescriptorSets(&info, &set);

            if (result == VkResult.Success) return set;

            if (result is not (VkResult.ErrorOutOfPoolMemory or VkResult.ErrorFragmentedPool))
                throw new InvalidOperationException($"The GPU could not make a descriptor set: {result}.");

            _poolInUse[slot]++;
        }
    }

    /// <summary>
    /// Copies a stage's constants into this frame's ring and returns the offset its draws read them at.
    /// </summary>
    internal uint Push(int slot, ReadOnlySpan<byte> constants)
    {
        ulong offset = (_ringUsed[slot] + _alignment - 1) / _alignment * _alignment;
        if (_rings[slot] is not { } ring || offset + _range > ring.Size)
        {
            ring = Grow(slot, offset + _range);
            offset = 0;
        }

        constants.CopyTo(new Span<byte>(ring.Mapped + offset, constants.Length));
        _ringUsed[slot] = offset + (ulong)constants.Length;

        return (uint)offset;
    }

    /// <summary>
    /// The set every stage's constants are read through this frame, over the ring as it is now.
    /// </summary>
    internal VkDescriptorSet GetUniformSet(int slot)
    {
        if (_uniformSets[slot].IsNotNull) return _uniformSets[slot];

        if (_rings[slot] is not { } ring) ring = Grow(slot, _range);

        VkDescriptorSet set = Allocate(slot, _pipelines.UniformLayout);
        VkDescriptorBufferInfo buffer = new() { buffer = ring.Handle, offset = 0, range = _range };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.UniformBufferDynamic,
            pBufferInfo = &buffer,
        };
        _device.Api.vkUpdateDescriptorSets(1, &write, 0, null);
        _uniformSets[slot] = set;

        return set;
    }

    /// <summary>
    /// A ring of at least <paramref name="bytes"/>, twice the last; the last stays until the frame that wrote it is done.
    /// </summary>
    private VulkanBuffer Grow(int slot, ulong bytes)
    {
        ulong size = InitialRingBytes;
        if (_rings[slot] is { } old)
        {
            _outgrown[slot].Add(old);
            size = old.Size * 2;
        }

        while (size < bytes)
        {
            size *= 2;
        }

        VulkanBuffer ring = _resources.CreateBuffer(size, VkBufferUsageFlags.UniformBuffer, isHost: true);
        _rings[slot] = ring;
        _ringUsed[slot] = 0;
        _uniformSets[slot] = VkDescriptorSet.Null;

        return ring;
    }

    private VkDescriptorPool CreatePool()
    {
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[]
        {
            new VkDescriptorPoolSize(VkDescriptorType.CombinedImageSampler, SetsPerPool * 8),
            new VkDescriptorPoolSize(VkDescriptorType.SampledImage, SetsPerPool * 4),
            new VkDescriptorPoolSize(VkDescriptorType.StorageImage, SetsPerPool * 2),
            new VkDescriptorPoolSize(VkDescriptorType.StorageBuffer, SetsPerPool * 8),
            new VkDescriptorPoolSize(VkDescriptorType.UniformBufferDynamic, SetsPerPool / 16),
        };
        VkDescriptorPoolCreateInfo info = new() { maxSets = SetsPerPool, poolSizeCount = 5, pPoolSizes = sizes };
        VkDescriptorPool pool;
        VkResult result = _device.Api.vkCreateDescriptorPool(&info, &pool);

        return result == VkResult.Success
            ? pool
            : throw new InvalidOperationException($"The GPU could not make a descriptor pool: {result}.");
    }
}
