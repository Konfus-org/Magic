using Magic.Graphics;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The bindless heap: one descriptor set every shader reads, an array per kind of resource, each resource at the
/// index its handle holds. A texture takes its index in every array its kind and format can be read through, and a
/// slot per mip level in the storage arrays, so one id reaches it however a shader declares it. The set is bound once
/// per pass and never changes shape; what is written into it changes as resources come and go.
/// </summary>
internal sealed unsafe class VulkanHeap : IDisposable
{
    /// <summary>
    /// The most textures the heap holds; a texture's id is below this.
    /// </summary>
    public const uint Textures = 4096;

    public const uint Buffers = 16384;

    public const uint Samplers = 256;

    /// <summary>
    /// The most acceleration structures the heap holds, on a GPU that traces rays.
    /// </summary>
    public const uint Accelerations = 256;

    /// <summary>
    /// Mip levels each texture has room for in the storage arrays: level n of texture t is at t * this + n.
    /// </summary>
    public const uint LevelsPerTexture = 16;

    /// <summary>
    /// Bytes of push constants: what <see cref="GpuConstants"/> holds.
    /// </summary>
    public const uint ConstantsBytes = 16;

    /// <summary>
    /// The stages the push constants reach: every stage a pipeline here has.
    /// </summary>
    public const VkShaderStageFlags PushStages = VkShaderStageFlags.AllGraphics | VkShaderStageFlags.Compute;

    private const uint BufferBinding = 0;
    private const uint Texture2DBinding = 1;
    private const uint Texture2DArrayBinding = 2;
    private const uint CubeBinding = 3;
    private const uint Texture3DBinding = 4;
    private const uint UintTexture2DBinding = 5;
    private const uint Storage2DBinding = 6;
    private const uint Storage2DArrayBinding = 7;
    private const uint Storage3DBinding = 8;
    private const uint UintStorage2DBinding = 9;
    private const uint SamplerBinding = 10;
    private const uint AccelerationBinding = 11;

    private readonly VkDeviceApi _api;
    private readonly VkDescriptorSetLayout _layout;
    private readonly VkDescriptorPool _pool;

    public VulkanHeap(VkDeviceApi api, bool hasAcceleration)
    {
        _api = api;
        uint levels = Textures * LevelsPerTexture;
        // The acceleration structures join only when the GPU traces rays, as their descriptor type needs its extension.
        Span<VkDescriptorSetLayoutBinding> bindings =
        [
            Binding(BufferBinding, VkDescriptorType.StorageBuffer, Buffers),
            Binding(Texture2DBinding, VkDescriptorType.SampledImage, Textures),
            Binding(Texture2DArrayBinding, VkDescriptorType.SampledImage, Textures),
            Binding(CubeBinding, VkDescriptorType.SampledImage, Textures),
            Binding(Texture3DBinding, VkDescriptorType.SampledImage, Textures),
            Binding(UintTexture2DBinding, VkDescriptorType.SampledImage, Textures),
            Binding(Storage2DBinding, VkDescriptorType.StorageImage, levels),
            Binding(Storage2DArrayBinding, VkDescriptorType.StorageImage, levels),
            Binding(Storage3DBinding, VkDescriptorType.StorageImage, levels),
            Binding(UintStorage2DBinding, VkDescriptorType.StorageImage, levels),
            Binding(SamplerBinding, VkDescriptorType.Sampler, Samplers),
            Binding(AccelerationBinding, VkDescriptorType.AccelerationStructureKHR, Accelerations),
        ];
        if (!hasAcceleration)
            bindings = bindings[..^1];

        Span<VkDescriptorBindingFlags> flags = stackalloc VkDescriptorBindingFlags[bindings.Length];
        flags.Fill(
            VkDescriptorBindingFlags.UpdateAfterBind
                | VkDescriptorBindingFlags.PartiallyBound
                | VkDescriptorBindingFlags.UpdateUnusedWhilePending
        );
        fixed (VkDescriptorSetLayoutBinding* bindingsPointer = bindings)
        fixed (VkDescriptorBindingFlags* flagsPointer = flags)
        {
            VkDescriptorSetLayoutBindingFlagsCreateInfo flagsInfo = new()
            {
                bindingCount = (uint)bindings.Length,
                pBindingFlags = flagsPointer,
            };
            VkDescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                pNext = &flagsInfo,
                flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPool,
                bindingCount = (uint)bindings.Length,
                pBindings = bindingsPointer,
            };
            _api.vkCreateDescriptorSetLayout(&layoutInfo, out _layout).ThrowIfFailed("the heap's layout");
        }

        Span<VkDescriptorPoolSize> sizes =
        [
            new(VkDescriptorType.StorageBuffer, Buffers),
            new(VkDescriptorType.SampledImage, Textures * 5),
            new(VkDescriptorType.StorageImage, levels * 4),
            new(VkDescriptorType.Sampler, Samplers),
            new(VkDescriptorType.AccelerationStructureKHR, Accelerations),
        ];
        if (!hasAcceleration)
            sizes = sizes[..^1];

        fixed (VkDescriptorPoolSize* sizesPointer = sizes)
        {
            VkDescriptorPoolCreateInfo poolInfo = new()
            {
                flags = VkDescriptorPoolCreateFlags.UpdateAfterBind,
                maxSets = 1,
                poolSizeCount = (uint)sizes.Length,
                pPoolSizes = sizesPointer,
            };
            _api.vkCreateDescriptorPool(&poolInfo, out _pool).ThrowIfFailed("the heap's pool");
        }

        VkDescriptorSetLayout layout = _layout;
        VkDescriptorSetAllocateInfo setInfo = new()
        {
            descriptorPool = _pool,
            descriptorSetCount = 1,
            pSetLayouts = &layout,
        };
        VkDescriptorSet set;
        _api.vkAllocateDescriptorSets(&setInfo, &set).ThrowIfFailed("the heap's set");
        Set = set;

        VkPushConstantRange constants = new()
        {
            stageFlags = PushStages,
            offset = 0,
            size = ConstantsBytes,
        };
        VkPipelineLayoutCreateInfo pipelineLayoutInfo = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &layout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &constants,
        };
        _api.vkCreatePipelineLayout(&pipelineLayoutInfo, out VkPipelineLayout pipelineLayout)
            .ThrowIfFailed("the pipeline layout");
        PipelineLayout = pipelineLayout;
    }

    public VkDescriptorSet Set { get; }

    /// <summary>
    /// The one layout every pipeline is built with: the heap's set and the push constants.
    /// </summary>
    public VkPipelineLayout PipelineLayout { get; }

    public void Dispose()
    {
        _api.vkDestroyPipelineLayout(PipelineLayout);
        _api.vkDestroyDescriptorPool(_pool);
        _api.vkDestroyDescriptorSetLayout(_layout);
    }

    /// <summary>
    /// Puts an acceleration structure where shaders trace through it.
    /// </summary>
    public void WriteAcceleration(uint id, VkAccelerationStructureKHR acceleration)
    {
        VkWriteDescriptorSetAccelerationStructureKHR info = new()
        {
            accelerationStructureCount = 1,
            pAccelerationStructures = &acceleration,
        };
        VkWriteDescriptorSet write = new()
        {
            pNext = &info,
            dstSet = Set,
            dstBinding = AccelerationBinding,
            dstArrayElement = id,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.AccelerationStructureKHR,
        };
        _api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    public void WriteBuffer(uint id, VkBuffer buffer, ulong bytes)
    {
        VkDescriptorBufferInfo info = new()
        {
            buffer = buffer,
            offset = 0,
            range = bytes,
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = Set,
            dstBinding = BufferBinding,
            dstArrayElement = id,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageBuffer,
            pBufferInfo = &info,
        };
        _api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    /// <summary>
    /// Puts a texture where its kind and format are read: its whole view in the sampled array of its kind, and each
    /// level's view in the storage array of its kind, when it can be written.
    /// </summary>
    public void WriteTexture(uint id, in TextureDescription description, VkImageView sampled, VkImageView[] levels)
    {
        bool isInteger = description.Format.IsInteger;
        uint sampledBinding = description.Kind switch
        {
            TextureKind.Texture2D => isInteger ? UintTexture2DBinding : Texture2DBinding,
            TextureKind.Texture2DArray => Texture2DArrayBinding,
            TextureKind.Cube => CubeBinding,
            TextureKind.Texture3D => Texture3DBinding,
            _ => throw new ArgumentOutOfRangeException(nameof(description), description.Kind, "There is no such kind."),
        };
        uint storageBinding = description.Kind switch
        {
            TextureKind.Texture2D => isInteger ? UintStorage2DBinding : Storage2DBinding,
            TextureKind.Texture2DArray or TextureKind.Cube => Storage2DArrayBinding,
            TextureKind.Texture3D => Storage3DBinding,
            _ => throw new ArgumentOutOfRangeException(nameof(description), description.Kind, "There is no such kind."),
        };
        bool isSampled = description.Usage.HasFlag(TextureUsages.Sampled);
        bool isStorage = description.Usage.HasFlag(TextureUsages.Storage);
        int count = (isSampled ? 1 : 0) + (isStorage ? levels.Length : 0);
        if (count == 0)
            return;

        VkDescriptorImageInfo* infos = stackalloc VkDescriptorImageInfo[count];
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[count];
        int written = 0;
        if (isSampled)
        {
            infos[written] = new VkDescriptorImageInfo
            {
                imageView = sampled,
                imageLayout = description.Format.IsDepth
                    ? VkImageLayout.DepthReadOnlyOptimal
                    : VkImageLayout.ShaderReadOnlyOptimal,
            };
            writes[written] = new VkWriteDescriptorSet
            {
                dstSet = Set,
                dstBinding = sampledBinding,
                dstArrayElement = id,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.SampledImage,
                pImageInfo = &infos[written],
            };
            written++;
        }

        if (isStorage)
        {
            for (uint level = 0; level < levels.Length; level++)
            {
                infos[written] = new VkDescriptorImageInfo
                {
                    imageView = levels[level],
                    imageLayout = VkImageLayout.General,
                };
                writes[written] = new VkWriteDescriptorSet
                {
                    dstSet = Set,
                    dstBinding = storageBinding,
                    dstArrayElement = (id * LevelsPerTexture) + level,
                    descriptorCount = 1,
                    descriptorType = VkDescriptorType.StorageImage,
                    pImageInfo = &infos[written],
                };
                written++;
            }
        }

        _api.vkUpdateDescriptorSets((uint)written, writes, 0, null);
    }

    public void WriteSampler(uint id, VkSampler sampler)
    {
        VkDescriptorImageInfo info = new() { sampler = sampler };
        VkWriteDescriptorSet write = new()
        {
            dstSet = Set,
            dstBinding = SamplerBinding,
            dstArrayElement = id,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.Sampler,
            pImageInfo = &info,
        };
        _api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private static VkDescriptorSetLayoutBinding Binding(uint binding, VkDescriptorType type, uint count) =>
        new()
        {
            binding = binding,
            descriptorType = type,
            descriptorCount = count,
            stageFlags = PushStages,
        };
}
