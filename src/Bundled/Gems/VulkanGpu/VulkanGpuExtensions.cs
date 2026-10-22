using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Gpu;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// Magic's GPU types and this gem's settings as Vulkan's, and the one way back: the format of the window's images.
/// </summary>
[SuppressMessage(
    "Style",
    "IDE0072:Add missing cases",
    Justification = "Vulkan has far more formats than Magic renders with; the rest are no format of Magic's.")]
internal static class VulkanGpuExtensions
{
    extension(GpuFormat format)
    {
        internal VkFormat ToVulkan() => format switch
        {
            GpuFormat.Invalid => VkFormat.Undefined,
            GpuFormat.R8Unorm => VkFormat.R8Unorm,
            GpuFormat.Rg8Unorm => VkFormat.R8G8Unorm,
            GpuFormat.Rgba8Unorm => VkFormat.R8G8B8A8Unorm,
            GpuFormat.Rgba8Srgb => VkFormat.R8G8B8A8Srgb,
            GpuFormat.Bgra8Unorm => VkFormat.B8G8R8A8Unorm,
            GpuFormat.Bgra8Srgb => VkFormat.B8G8R8A8Srgb,
            GpuFormat.Rgb10A2Unorm => VkFormat.A2B10G10R10UnormPack32,
            GpuFormat.R16Float => VkFormat.R16Sfloat,
            GpuFormat.Rg16Float => VkFormat.R16G16Sfloat,
            GpuFormat.Rgba16Float => VkFormat.R16G16B16A16Sfloat,
            GpuFormat.R32Float => VkFormat.R32Sfloat,
            GpuFormat.Rg32Float => VkFormat.R32G32Sfloat,
            GpuFormat.Rgba32Float => VkFormat.R32G32B32A32Sfloat,
            GpuFormat.R11G11B10Float => VkFormat.B10G11R11UfloatPack32,
            GpuFormat.R32Uint => VkFormat.R32Uint,
            GpuFormat.D32Float => VkFormat.D32Sfloat,
            GpuFormat.Bc7Unorm => VkFormat.Bc7UnormBlock,
            GpuFormat.Bc7Srgb => VkFormat.Bc7SrgbBlock,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such GPU format."),
        };

        /// <summary>
        /// Which of the image's aspects a view or a copy of it reads: depth for a depth format, colour for the rest.
        /// </summary>
        internal VkImageAspectFlags ToAspect() => format == GpuFormat.D32Float
            ? VkImageAspectFlags.Depth
            : VkImageAspectFlags.Color;
    }

    extension(VkFormat format)
    {
        /// <summary>
        /// A window image's format as Magic's: the encoding the last pass of a frame writes follows from it.
        /// </summary>
        internal GpuFormat ToMagic() => format switch
        {
            VkFormat.R8G8B8A8Unorm => GpuFormat.Rgba8Unorm,
            VkFormat.R8G8B8A8Srgb => GpuFormat.Rgba8Srgb,
            VkFormat.B8G8R8A8Unorm => GpuFormat.Bgra8Unorm,
            VkFormat.B8G8R8A8Srgb => GpuFormat.Bgra8Srgb,
            VkFormat.A2B10G10R10UnormPack32 => GpuFormat.Rgb10A2Unorm,
            VkFormat.R16G16B16A16Sfloat => GpuFormat.Rgba16Float,
            _ => GpuFormat.Invalid,
        };
    }

    extension(Composition composition)
    {
        /// <summary>
        /// The colour space a window composed so presents in, and the formats that compose it, preferred first.
        /// </summary>
        internal (VkColorSpaceKHR Space, VkFormat First, VkFormat Second) ToVulkan() => composition switch
        {
            Composition.SdrLinear => (VkColorSpaceKHR.SrgbNonLinear, VkFormat.B8G8R8A8Srgb, VkFormat.R8G8B8A8Srgb),
            Composition.Hdr10 => (
                VkColorSpaceKHR.Hdr10St2084EXT,
                VkFormat.A2B10G10R10UnormPack32,
                VkFormat.A2B10G10R10UnormPack32),
            Composition.HdrLinear => (
                VkColorSpaceKHR.ExtendedSrgbLinearEXT,
                VkFormat.R16G16B16A16Sfloat,
                VkFormat.R16G16B16A16Sfloat),
            Composition.Sdr => (VkColorSpaceKHR.SrgbNonLinear, VkFormat.B8G8R8A8Unorm, VkFormat.R8G8B8A8Unorm),
            _ => throw new ArgumentOutOfRangeException(
                nameof(composition),
                composition,
                "There is no such composition."),
        };
    }

    extension(GpuBufferUsage usage)
    {
        /// <summary>
        /// What Vulkan is told a buffer is for: always copied into and out of, as uploads, copies and reads are; read
        /// or written by shaders as a storage buffer.
        /// </summary>
        internal VkBufferUsageFlags ToVulkan()
        {
            const GpuBufferUsage Shaders = GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead
                | GpuBufferUsage.ComputeWrite;
            VkBufferUsageFlags flags = VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst;
            if ((usage & GpuBufferUsage.Vertex) != GpuBufferUsage.None) flags |= VkBufferUsageFlags.VertexBuffer;
            if ((usage & GpuBufferUsage.Index) != GpuBufferUsage.None) flags |= VkBufferUsageFlags.IndexBuffer;
            if ((usage & GpuBufferUsage.Arguments) != GpuBufferUsage.None) flags |= VkBufferUsageFlags.IndirectBuffer;
            if ((usage & Shaders) != GpuBufferUsage.None) flags |= VkBufferUsageFlags.StorageBuffer;

            return flags;
        }
    }

    extension(GpuTextureUsage usage)
    {
        /// <summary>
        /// What Vulkan is told an image is for: always copied, blitted for its mips and read back; sampled wherever a
        /// shader reads it, texel by texel or filtered, as a shader declares a texture it only reads as a sampled one;
        /// a storage image where compute writes it.
        /// </summary>
        internal VkImageUsageFlags ToVulkan()
        {
            const GpuTextureUsage Reads = GpuTextureUsage.Sampled | GpuTextureUsage.GraphicsRead
                | GpuTextureUsage.ComputeRead;
            VkImageUsageFlags flags = VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst;
            if ((usage & Reads) != GpuTextureUsage.None) flags |= VkImageUsageFlags.Sampled;
            if ((usage
                & GpuTextureUsage.ColorTarget) != GpuTextureUsage.None)
            {
                flags |= VkImageUsageFlags.ColorAttachment;
            }

            if ((usage & GpuTextureUsage.DepthTarget) != GpuTextureUsage.None)
            {
                flags |= VkImageUsageFlags.DepthStencilAttachment;
            }

            if ((usage & GpuTextureUsage.ComputeWrite) != GpuTextureUsage.None) flags |= VkImageUsageFlags.Storage;

            return flags;
        }
    }

    extension(GpuTextureKind kind)
    {
        internal VkImageType ToVulkan() => kind == GpuTextureKind.Texture3D ? VkImageType.Image3D : VkImageType.Image2D;

        /// <summary>
        /// How a shader sees the whole texture, every level and layer, as it samples it.
        /// </summary>
        internal VkImageViewType ToSampledView() => kind switch
        {
            GpuTextureKind.Texture2D => VkImageViewType.Image2D,
            GpuTextureKind.Texture2DArray => VkImageViewType.Image2DArray,
            GpuTextureKind.Texture3D => VkImageViewType.Image3D,
            GpuTextureKind.Cube => VkImageViewType.ImageCube,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such texture kind."),
        };
    }

    extension(GpuFilter filter)
    {
        internal VkFilter ToVulkan() => filter switch
        {
            GpuFilter.Linear => VkFilter.Linear,
            GpuFilter.Nearest => VkFilter.Nearest,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "There is no such filter."),
        };

        internal VkSamplerMipmapMode ToVulkanMipmaps() => filter switch
        {
            GpuFilter.Linear => VkSamplerMipmapMode.Linear,
            GpuFilter.Nearest => VkSamplerMipmapMode.Nearest,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "There is no such filter."),
        };
    }

    extension(GpuWrap wrap)
    {
        internal VkSamplerAddressMode ToVulkan() => wrap switch
        {
            GpuWrap.Clamp => VkSamplerAddressMode.ClampToEdge,
            GpuWrap.Repeat => VkSamplerAddressMode.Repeat,
            GpuWrap.Mirror => VkSamplerAddressMode.MirroredRepeat,
            _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, "There is no such wrap."),
        };
    }

    extension(GpuCompare compare)
    {
        internal VkCompareOp ToVulkan() => compare switch
        {
            GpuCompare.Never => VkCompareOp.Never,
            GpuCompare.Less => VkCompareOp.Less,
            GpuCompare.Equal => VkCompareOp.Equal,
            GpuCompare.LessOrEqual => VkCompareOp.LessOrEqual,
            GpuCompare.Greater => VkCompareOp.Greater,
            GpuCompare.NotEqual => VkCompareOp.NotEqual,
            GpuCompare.GreaterOrEqual => VkCompareOp.GreaterOrEqual,
            GpuCompare.Always => VkCompareOp.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(compare), compare, "There is no such comparison."),
        };
    }

    extension(GpuVertexFormat format)
    {
        internal VkFormat ToVulkan() => format switch
        {
            GpuVertexFormat.Float1 => VkFormat.R32Sfloat,
            GpuVertexFormat.Float2 => VkFormat.R32G32Sfloat,
            GpuVertexFormat.Float3 => VkFormat.R32G32B32Sfloat,
            GpuVertexFormat.Float4 => VkFormat.R32G32B32A32Sfloat,
            GpuVertexFormat.Uint1 => VkFormat.R32Uint,
            GpuVertexFormat.Ubyte4Norm => VkFormat.R8G8B8A8Unorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such vertex format."),
        };
    }

    extension(GpuCull cull)
    {
        internal VkCullModeFlags ToVulkan() => cull switch
        {
            GpuCull.None => VkCullModeFlags.None,
            GpuCull.Back => VkCullModeFlags.Back,
            GpuCull.Front => VkCullModeFlags.Front,
            _ => throw new ArgumentOutOfRangeException(nameof(cull), cull, "There is no such cull mode."),
        };
    }

    extension(GpuContents contents)
    {
        internal VkAttachmentLoadOp ToVulkan() => contents switch
        {
            GpuContents.Keep => VkAttachmentLoadOp.Load,
            GpuContents.Clear => VkAttachmentLoadOp.Clear,
            GpuContents.Discard => VkAttachmentLoadOp.DontCare,
            _ => throw new ArgumentOutOfRangeException(nameof(contents), contents, "There is no such contents."),
        };
    }

    extension(GpuBlend blend)
    {
        internal VkPipelineColorBlendAttachmentState ToVulkan() => blend switch
        {
            GpuBlend.None => new VkPipelineColorBlendAttachmentState { colorWriteMask = VkColorComponentFlags.All },
            GpuBlend.Alpha => MakeBlending(VkBlendFactor.SrcAlpha, VkBlendFactor.OneMinusSrcAlpha, VkBlendOp.Add),
            GpuBlend.Premultiplied => MakeBlending(VkBlendFactor.One, VkBlendFactor.OneMinusSrcAlpha, VkBlendOp.Add),
            GpuBlend.Add => MakeBlending(VkBlendFactor.One, VkBlendFactor.One, VkBlendOp.Add),
            GpuBlend.Max => MakeBlending(VkBlendFactor.One, VkBlendFactor.One, VkBlendOp.Max),
            _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "There is no such blend."),
        };
    }

    /// <summary>
    /// Colour blends <paramref name="source"/> and <paramref name="destination"/> with <paramref name="operation"/>;
    /// alpha does the same, except that straight alpha keeps its own: one of src, one minus src of dst.
    /// </summary>
    private static VkPipelineColorBlendAttachmentState MakeBlending(
        VkBlendFactor source,
        VkBlendFactor destination,
        VkBlendOp operation) => new()
        {
            blendEnable = true,
            srcColorBlendFactor = source,
            dstColorBlendFactor = destination,
            colorBlendOp = operation,
            srcAlphaBlendFactor = source == VkBlendFactor.SrcAlpha ? VkBlendFactor.One : source,
            dstAlphaBlendFactor = destination,
            alphaBlendOp = operation,
            colorWriteMask = VkColorComponentFlags.All,
        };
}
