using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Gpu;
using SDL;

namespace Magic.SdlGpu;

/// <summary>
/// Magic's GPU types as SDL's, and the one way back: the format of the window's images.
/// </summary>
[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "SDL has far more texture formats than Magic renders with; the rest are no format of Magic's.")]
internal static class SdlGpuExtensions
{
    extension(GpuFormat format)
    {
        public SDL_GPUTextureFormat ToSdl() => format switch
        {
            GpuFormat.Invalid => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_INVALID,
            GpuFormat.R8Unorm => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8_UNORM,
            GpuFormat.Rg8Unorm => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8_UNORM,
            GpuFormat.Rgba8Unorm => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            GpuFormat.Rgba8Srgb => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB,
            GpuFormat.Bgra8Unorm => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM,
            GpuFormat.Bgra8Srgb => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM_SRGB,
            GpuFormat.Rgb10A2Unorm => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R10G10B10A2_UNORM,
            GpuFormat.R16Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R16_FLOAT,
            GpuFormat.Rg16Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R16G16_FLOAT,
            GpuFormat.Rgba16Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT,
            GpuFormat.R32Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R32_FLOAT,
            GpuFormat.Rg32Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R32G32_FLOAT,
            GpuFormat.Rgba32Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R32G32B32A32_FLOAT,
            GpuFormat.R11G11B10Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R11G11B10_UFLOAT,
            GpuFormat.R32Uint => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R32_UINT,
            GpuFormat.D32Float => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
            GpuFormat.Bc7Unorm => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_BC7_RGBA_UNORM,
            GpuFormat.Bc7Srgb => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_BC7_RGBA_UNORM_SRGB,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such GPU format."),
        };
    }

    extension(SDL_GPUTextureFormat format)
    {
        public GpuFormat ToMagic() => format switch
        {
            SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM => GpuFormat.Rgba8Unorm,
            SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB => GpuFormat.Rgba8Srgb,
            SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM => GpuFormat.Bgra8Unorm,
            SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM_SRGB => GpuFormat.Bgra8Srgb,
            SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R10G10B10A2_UNORM => GpuFormat.Rgb10A2Unorm,
            SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT => GpuFormat.Rgba16Float,
            _ => GpuFormat.Invalid,
        };
    }

    extension(GpuShaderFormat format)
    {
        public SDL_GPUShaderFormat ToSdl() => format switch
        {
            GpuShaderFormat.Spirv => SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV,
            GpuShaderFormat.Dxil => SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL,
            GpuShaderFormat.Msl => SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such shader format."),
        };
    }

    extension(GpuBufferUsage usage)
    {
        /// <summary>
        /// Magic's usage bits are SDL's, in the same order.
        /// </summary>
        public SDL_GPUBufferUsageFlags ToSdl() => (SDL_GPUBufferUsageFlags)usage;
    }

    extension(GpuTextureUsage usage)
    {
        /// <summary>
        /// Magic's usage bits are SDL's, in the same order.
        /// </summary>
        public SDL_GPUTextureUsageFlags ToSdl() => (SDL_GPUTextureUsageFlags)usage;
    }

    extension(GpuTextureKind kind)
    {
        public SDL_GPUTextureType ToSdl() => kind switch
        {
            GpuTextureKind.Texture2D => SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            GpuTextureKind.Texture2DArray => SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D_ARRAY,
            GpuTextureKind.Texture3D => SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_3D,
            GpuTextureKind.Cube => SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_CUBE,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such texture kind."),
        };
    }

    extension(GpuFilter filter)
    {
        public SDL_GPUFilter ToSdl() => filter switch
        {
            GpuFilter.Linear => SDL_GPUFilter.SDL_GPU_FILTER_LINEAR,
            GpuFilter.Nearest => SDL_GPUFilter.SDL_GPU_FILTER_NEAREST,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "There is no such filter."),
        };

        public SDL_GPUSamplerMipmapMode ToSdlMipmaps() => filter switch
        {
            GpuFilter.Linear => SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR,
            GpuFilter.Nearest => SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "There is no such filter."),
        };
    }

    extension(GpuWrap wrap)
    {
        public SDL_GPUSamplerAddressMode ToSdl() => wrap switch
        {
            GpuWrap.Clamp => SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            GpuWrap.Repeat => SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
            GpuWrap.Mirror => SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_MIRRORED_REPEAT,
            _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, "There is no such wrap."),
        };
    }

    extension(GpuCompare compare)
    {
        public SDL_GPUCompareOp ToSdl() => compare switch
        {
            GpuCompare.Never => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_NEVER,
            GpuCompare.Less => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS,
            GpuCompare.Equal => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_EQUAL,
            GpuCompare.LessOrEqual => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
            GpuCompare.Greater => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_GREATER,
            GpuCompare.NotEqual => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_NOT_EQUAL,
            GpuCompare.GreaterOrEqual => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_GREATER_OR_EQUAL,
            GpuCompare.Always => SDL_GPUCompareOp.SDL_GPU_COMPAREOP_ALWAYS,
            _ => throw new ArgumentOutOfRangeException(nameof(compare), compare, "There is no such comparison."),
        };
    }

    extension(GpuVertexFormat format)
    {
        public SDL_GPUVertexElementFormat ToSdl() => format switch
        {
            GpuVertexFormat.Float1 => SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT,
            GpuVertexFormat.Float2 => SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2,
            GpuVertexFormat.Float3 => SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3,
            GpuVertexFormat.Float4 => SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4,
            GpuVertexFormat.Uint1 => SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_UINT,
            GpuVertexFormat.Ubyte4Norm => SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4_NORM,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such vertex format."),
        };
    }

    extension(GpuCull cull)
    {
        public SDL_GPUCullMode ToSdl() => cull switch
        {
            GpuCull.None => SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE,
            GpuCull.Back => SDL_GPUCullMode.SDL_GPU_CULLMODE_BACK,
            GpuCull.Front => SDL_GPUCullMode.SDL_GPU_CULLMODE_FRONT,
            _ => throw new ArgumentOutOfRangeException(nameof(cull), cull, "There is no such cull mode."),
        };
    }

    extension(GpuContents contents)
    {
        public SDL_GPULoadOp ToSdl() => contents switch
        {
            GpuContents.Keep => SDL_GPULoadOp.SDL_GPU_LOADOP_LOAD,
            GpuContents.Clear => SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
            GpuContents.Discard => SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
            _ => throw new ArgumentOutOfRangeException(nameof(contents), contents, "There is no such contents."),
        };
    }

    extension(GpuBlend blend)
    {
        public SDL_GPUColorTargetBlendState ToSdl() => blend switch
        {
            GpuBlend.None => default,
            GpuBlend.Alpha => Blending(SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA, SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD),
            GpuBlend.Premultiplied => Blending(SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA, SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD),
            GpuBlend.Add => Blending(SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE, SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD),
            GpuBlend.Max => Blending(SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE, SDL_GPUBlendOp.SDL_GPU_BLENDOP_MAX),
            _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "There is no such blend."),
        };
    }

    /// <summary>
    /// Colour blends <paramref name="source"/> and <paramref name="destination"/> with <paramref name="operation"/>;
    /// alpha does the same, except that straight alpha keeps its own: one of src, one minus src of dst.
    /// </summary>
    private static SDL_GPUColorTargetBlendState Blending(SDL_GPUBlendFactor source, SDL_GPUBlendFactor destination, SDL_GPUBlendOp operation) => new()
    {
        enable_blend = true,
        src_color_blendfactor = source,
        dst_color_blendfactor = destination,
        color_blend_op = operation,
        src_alpha_blendfactor = source == SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA ? SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE : source,
        dst_alpha_blendfactor = destination,
        alpha_blend_op = operation,
    };
}
