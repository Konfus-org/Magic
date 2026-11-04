using System.Collections.Frozen;
using Magic.Files.Types;
using Magic.Gpu;
using Vortice.Vulkan;

namespace VulkanGraphics;

/// <summary>
/// Core's GPU words as Vulkan's: formats, usages, accesses with the stage, access and layout each implies. Pure
/// functions, one per word.
/// </summary>
internal static class VulkanTranslate
{
    /// <summary>
    /// The engine's word for each format a swapchain may come in.
    /// </summary>
    private static readonly FrozenDictionary<VkFormat, GpuFormat> _windowFormats = new Dictionary<VkFormat, GpuFormat>
    {
        [VkFormat.R8G8B8A8Unorm] = GpuFormat.R8G8B8A8Unorm,
        [VkFormat.R8G8B8A8Srgb] = GpuFormat.R8G8B8A8Srgb,
        [VkFormat.B8G8R8A8Unorm] = GpuFormat.B8G8R8A8Unorm,
        [VkFormat.B8G8R8A8Srgb] = GpuFormat.B8G8R8A8Srgb,
        [VkFormat.A2B10G10R10UnormPack32] = GpuFormat.A2B10G10R10Unorm,
        [VkFormat.R16G16B16A16Sfloat] = GpuFormat.R16G16B16A16Sfloat,
    }.ToFrozenDictionary();

    public static VkFormat ToVulkan(this GpuFormat format) =>
        format switch
        {
            GpuFormat.Unknown => VkFormat.Undefined,
            GpuFormat.R8Unorm => VkFormat.R8Unorm,
            GpuFormat.R8Snorm => VkFormat.R8Snorm,
            GpuFormat.R8G8Unorm => VkFormat.R8G8Unorm,
            GpuFormat.R8G8B8A8Unorm => VkFormat.R8G8B8A8Unorm,
            GpuFormat.R8G8B8A8Srgb => VkFormat.R8G8B8A8Srgb,
            GpuFormat.B8G8R8A8Unorm => VkFormat.B8G8R8A8Unorm,
            GpuFormat.B8G8R8A8Srgb => VkFormat.B8G8R8A8Srgb,
            GpuFormat.A2B10G10R10Unorm => VkFormat.A2B10G10R10UnormPack32,
            GpuFormat.B10G11R11Ufloat => VkFormat.B10G11R11UfloatPack32,
            GpuFormat.R16Uint => VkFormat.R16Uint,
            GpuFormat.R16G16Unorm => VkFormat.R16G16Unorm,
            GpuFormat.R16G16Sfloat => VkFormat.R16G16Sfloat,
            GpuFormat.R16G16B16A16Sfloat => VkFormat.R16G16B16A16Sfloat,
            GpuFormat.R32Uint => VkFormat.R32Uint,
            GpuFormat.R32Sfloat => VkFormat.R32Sfloat,
            GpuFormat.R32G32Uint => VkFormat.R32G32Uint,
            GpuFormat.R32G32Sfloat => VkFormat.R32G32Sfloat,
            GpuFormat.R32G32B32A32Sfloat => VkFormat.R32G32B32A32Sfloat,
            GpuFormat.D32Sfloat => VkFormat.D32Sfloat,
            GpuFormat.Bc4Unorm => VkFormat.Bc4UnormBlock,
            GpuFormat.Bc5Unorm => VkFormat.Bc5UnormBlock,
            GpuFormat.Bc7Unorm => VkFormat.Bc7UnormBlock,
            GpuFormat.Bc7Srgb => VkFormat.Bc7SrgbBlock,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such format."),
        };

    /// <summary>
    /// The engine's format for a swapchain's, or <see cref="GpuFormat.Unknown"/> for one the engine has no word for.
    /// </summary>
    public static GpuFormat ToGpu(this VkFormat format) => _windowFormats.GetValueOrDefault(format, GpuFormat.Unknown);

    public static VkBufferUsageFlags ToVulkan(this BufferUsages usage, bool hasAcceleration)
    {
        // Every buffer may be copied either way, so uploads and readbacks need nothing of their own.
        VkBufferUsageFlags flags = VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst;
        if (usage.HasFlag(BufferUsages.Storage))
            flags |= VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.ShaderDeviceAddress;

        // A storage buffer may hold the triangles or the instances an acceleration structure is built from.
        if (usage.HasFlag(BufferUsages.Storage) && hasAcceleration)
            flags |= VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyKHR;

        if (usage.HasFlag(BufferUsages.Acceleration))
            flags |= VkBufferUsageFlags.AccelerationStructureStorageKHR | VkBufferUsageFlags.ShaderDeviceAddress;

        if (usage.HasFlag(BufferUsages.Index))
            flags |= VkBufferUsageFlags.IndexBuffer;

        if (usage.HasFlag(BufferUsages.Indirect))
            flags |= VkBufferUsageFlags.IndirectBuffer;

        return flags;
    }

    public static VkImageUsageFlags ToVulkan(this TextureUsages usage)
    {
        VkImageUsageFlags flags = VkImageUsageFlags.None;
        if (usage.HasFlag(TextureUsages.Sampled))
            flags |= VkImageUsageFlags.Sampled;

        if (usage.HasFlag(TextureUsages.Storage))
            flags |= VkImageUsageFlags.Storage;

        if (usage.HasFlag(TextureUsages.ColorTarget))
            flags |= VkImageUsageFlags.ColorAttachment;

        if (usage.HasFlag(TextureUsages.DepthTarget))
            flags |= VkImageUsageFlags.DepthStencilAttachment;

        if (usage.HasFlag(TextureUsages.CopySource))
            flags |= VkImageUsageFlags.TransferSrc;

        if (usage.HasFlag(TextureUsages.CopyDestination))
            flags |= VkImageUsageFlags.TransferDst;

        return flags;
    }

    public static VkImageType ToImageType(this TextureKind kind) =>
        kind == TextureKind.Texture3D ? VkImageType.Image3D : VkImageType.Image2D;

    public static VkImageViewType ToViewType(this TextureKind kind) =>
        kind switch
        {
            TextureKind.Texture2D => VkImageViewType.Image2D,
            TextureKind.Texture2DArray => VkImageViewType.Image2DArray,
            TextureKind.Cube => VkImageViewType.ImageCube,
            TextureKind.Texture3D => VkImageViewType.Image3D,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such kind."),
        };

    public static VkFilter ToVulkan(this TextureFilter filter) =>
        filter == TextureFilter.Nearest ? VkFilter.Nearest : VkFilter.Linear;

    public static VkSamplerAddressMode ToVulkan(this TextureWrap wrap) =>
        wrap switch
        {
            TextureWrap.Clamp => VkSamplerAddressMode.ClampToEdge,
            TextureWrap.Repeat => VkSamplerAddressMode.Repeat,
            TextureWrap.Mirror => VkSamplerAddressMode.MirroredRepeat,
            _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, "There is no such wrap."),
        };

    public static VkSamplerReductionMode ToVulkan(this SamplerReduction reduction) =>
        reduction switch
        {
            SamplerReduction.Average => VkSamplerReductionMode.WeightedAverage,
            SamplerReduction.Min => VkSamplerReductionMode.Min,
            SamplerReduction.Max => VkSamplerReductionMode.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(reduction), reduction, "There is no such reduction."),
        };

    public static VkCullModeFlags ToVulkan(this CullMode cull) =>
        cull switch
        {
            CullMode.None => VkCullModeFlags.None,
            CullMode.Back => VkCullModeFlags.Back,
            CullMode.Front => VkCullModeFlags.Front,
            _ => throw new ArgumentOutOfRangeException(nameof(cull), cull, "There is no such cull mode."),
        };

    public static VkAttachmentLoadOp ToVulkan(this TargetLoad load) =>
        load switch
        {
            TargetLoad.Keep => VkAttachmentLoadOp.Load,
            TargetLoad.Clear => VkAttachmentLoadOp.Clear,
            TargetLoad.Discard => VkAttachmentLoadOp.DontCare,
            _ => throw new ArgumentOutOfRangeException(nameof(load), load, "There is no such load."),
        };

    /// <summary>
    /// The blend state a mode means, colours premultiplied.
    /// </summary>
    public static VkPipelineColorBlendAttachmentState ToBlendState(this BlendMode blend) =>
        blend switch
        {
            BlendMode.Opaque => new(
                false,
                VkBlendFactor.One,
                VkBlendFactor.Zero,
                VkBlendOp.Add,
                VkBlendFactor.One,
                VkBlendFactor.Zero,
                VkBlendOp.Add,
                VkColorComponentFlags.All
            ),
            BlendMode.Alpha => new(
                true,
                VkBlendFactor.One,
                VkBlendFactor.OneMinusSrcAlpha,
                VkBlendOp.Add,
                VkBlendFactor.One,
                VkBlendFactor.OneMinusSrcAlpha,
                VkBlendOp.Add,
                VkColorComponentFlags.All
            ),
            BlendMode.Additive => new(
                true,
                VkBlendFactor.One,
                VkBlendFactor.One,
                VkBlendOp.Add,
                VkBlendFactor.One,
                VkBlendFactor.One,
                VkBlendOp.Add,
                VkColorComponentFlags.All
            ),
            BlendMode.Multiply => new(
                true,
                VkBlendFactor.DstColor,
                VkBlendFactor.Zero,
                VkBlendOp.Add,
                VkBlendFactor.DstAlpha,
                VkBlendFactor.Zero,
                VkBlendOp.Add,
                VkColorComponentFlags.All
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "There is no such blend mode."),
        };

    /// <summary>
    /// The stages that touch a resource under <paramref name="access"/>, and how.
    /// </summary>
    public static (VkPipelineStageFlags2 Stages, VkAccessFlags2 Access) ToStageAndAccess(this GpuAccess access) =>
        access switch
        {
            GpuAccess.None => (VkPipelineStageFlags2.TopOfPipe, VkAccessFlags2.None),
            GpuAccess.ShaderRead => (
                VkPipelineStageFlags2.AllGraphics | VkPipelineStageFlags2.ComputeShader,
                VkAccessFlags2.ShaderSampledRead | VkAccessFlags2.ShaderStorageRead
            ),
            GpuAccess.ShaderWrite => (
                VkPipelineStageFlags2.AllGraphics | VkPipelineStageFlags2.ComputeShader,
                VkAccessFlags2.ShaderStorageRead | VkAccessFlags2.ShaderStorageWrite
            ),
            GpuAccess.ColorTarget => (
                VkPipelineStageFlags2.ColorAttachmentOutput,
                VkAccessFlags2.ColorAttachmentRead | VkAccessFlags2.ColorAttachmentWrite
            ),
            GpuAccess.DepthTarget => (
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite
            ),
            GpuAccess.DepthRead => (
                VkPipelineStageFlags2.EarlyFragmentTests
                    | VkPipelineStageFlags2.LateFragmentTests
                    | VkPipelineStageFlags2.FragmentShader
                    | VkPipelineStageFlags2.ComputeShader,
                VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.ShaderSampledRead
            ),
            GpuAccess.Indirect => (VkPipelineStageFlags2.DrawIndirect, VkAccessFlags2.IndirectCommandRead),
            GpuAccess.Index => (VkPipelineStageFlags2.VertexInput, VkAccessFlags2.IndexRead),
            GpuAccess.CopySource => (VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferRead),
            GpuAccess.CopyDestination => (VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite),
            GpuAccess.Present => (VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.None),
            GpuAccess.HostRead => (VkPipelineStageFlags2.Host, VkAccessFlags2.HostRead),
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, "There is no such access."),
        };

    /// <summary>
    /// The layout an image is kept in under <paramref name="access"/>.
    /// </summary>
    public static VkImageLayout ToLayout(this GpuAccess access, bool isDepth) =>
        access switch
        {
            GpuAccess.None => VkImageLayout.Undefined,
            GpuAccess.ShaderRead => isDepth ? VkImageLayout.DepthReadOnlyOptimal : VkImageLayout.ShaderReadOnlyOptimal,
            GpuAccess.ShaderWrite => VkImageLayout.General,
            GpuAccess.ColorTarget => VkImageLayout.ColorAttachmentOptimal,
            GpuAccess.DepthTarget => VkImageLayout.DepthAttachmentOptimal,
            GpuAccess.DepthRead => VkImageLayout.DepthReadOnlyOptimal,
            GpuAccess.Indirect => VkImageLayout.Undefined,
            GpuAccess.Index => VkImageLayout.Undefined,
            GpuAccess.CopySource => VkImageLayout.TransferSrcOptimal,
            GpuAccess.CopyDestination => VkImageLayout.TransferDstOptimal,
            GpuAccess.Present => VkImageLayout.PresentSrcKHR,
            GpuAccess.HostRead => VkImageLayout.Undefined,
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, "There is no such access."),
        };
}
