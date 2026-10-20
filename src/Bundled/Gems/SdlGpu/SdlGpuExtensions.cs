using System.Diagnostics.CodeAnalysis;
using Magic.SdlBindings;
using Magic.Contexts.Gpu;

namespace Magic.SdlGpu;

/// <summary>
/// Magic's GPU types and this gem's settings as SDL's, and the one way back: the format of the window's images.
/// </summary>
[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "SDL has far more texture formats than Magic renders with; the rest are no format of Magic's.")]
internal static class SdlGpuExtensions
{
    extension(GpuDriver driver)
    {
        /// <summary>
        /// SDL's name for the driver, what it is asked for by name; empty for <see cref="GpuDriver.Auto"/>, its own
        /// choice.
        /// </summary>
        internal string ToSdl() => driver switch
        {
            GpuDriver.Vulkan => "vulkan",
            GpuDriver.Direct3d12 => "direct3d12",
            GpuDriver.Metal => "metal",
            GpuDriver.Auto or _ => string.Empty,
        };
    }

    extension(Composition composition)
    {
        /// <summary>
        /// The composition as SDL names it, whether or not the window supports it.
        /// </summary>
        internal SDL.GPUSwapchainComposition ToSdl() => composition switch
        {
            Composition.SdrLinear => SDL.GPUSwapchainComposition.SdrLinear,
            Composition.Hdr10 => SDL.GPUSwapchainComposition.Hdr10St2084,
            Composition.HdrLinear => SDL.GPUSwapchainComposition.HdrExtendedLinear,
            Composition.Sdr or _ => SDL.GPUSwapchainComposition.Sdr,
        };
    }

    extension(GpuFormat format)
    {
        internal SDL.GPUTextureFormat ToSdl() => format switch
        {
            GpuFormat.Invalid => SDL.GPUTextureFormat.Invalid,
            GpuFormat.R8Unorm => SDL.GPUTextureFormat.R8Unorm,
            GpuFormat.Rg8Unorm => SDL.GPUTextureFormat.R8g8Unorm,
            GpuFormat.Rgba8Unorm => SDL.GPUTextureFormat.R8g8b8a8Unorm,
            GpuFormat.Rgba8Srgb => SDL.GPUTextureFormat.R8g8b8a8UnormSrgb,
            GpuFormat.Bgra8Unorm => SDL.GPUTextureFormat.B8g8r8a8Unorm,
            GpuFormat.Bgra8Srgb => SDL.GPUTextureFormat.B8g8r8a8UnormSrgb,
            GpuFormat.Rgb10A2Unorm => SDL.GPUTextureFormat.R10g10b10a2Unorm,
            GpuFormat.R16Float => SDL.GPUTextureFormat.R16Float,
            GpuFormat.Rg16Float => SDL.GPUTextureFormat.R16g16Float,
            GpuFormat.Rgba16Float => SDL.GPUTextureFormat.R16g16b16a16Float,
            GpuFormat.R32Float => SDL.GPUTextureFormat.R32Float,
            GpuFormat.Rg32Float => SDL.GPUTextureFormat.R32g32Float,
            GpuFormat.Rgba32Float => SDL.GPUTextureFormat.R32g32b32a32Float,
            GpuFormat.R11G11B10Float => SDL.GPUTextureFormat.R11g11b10Ufloat,
            GpuFormat.R32Uint => SDL.GPUTextureFormat.R32Uint,
            GpuFormat.D32Float => SDL.GPUTextureFormat.D32Float,
            GpuFormat.Bc7Unorm => SDL.GPUTextureFormat.Bc7RgbaUnorm,
            GpuFormat.Bc7Srgb => SDL.GPUTextureFormat.Bc7RgbaUnormSrgb,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such GPU format."),
        };
    }

    extension(SDL.GPUTextureFormat format)
    {
        internal GpuFormat ToMagic() => format switch
        {
            SDL.GPUTextureFormat.R8g8b8a8Unorm => GpuFormat.Rgba8Unorm,
            SDL.GPUTextureFormat.R8g8b8a8UnormSrgb => GpuFormat.Rgba8Srgb,
            SDL.GPUTextureFormat.B8g8r8a8Unorm => GpuFormat.Bgra8Unorm,
            SDL.GPUTextureFormat.B8g8r8a8UnormSrgb => GpuFormat.Bgra8Srgb,
            SDL.GPUTextureFormat.R10g10b10a2Unorm => GpuFormat.Rgb10A2Unorm,
            SDL.GPUTextureFormat.R16g16b16a16Float => GpuFormat.Rgba16Float,
            _ => GpuFormat.Invalid,
        };
    }

    extension(GpuShaderFormat format)
    {
        internal uint ToSdl() => format switch
        {
            GpuShaderFormat.Spirv => SDL.GpuShaderformatSpirv,
            GpuShaderFormat.Dxil => SDL.GpuShaderformatDxil,
            GpuShaderFormat.Msl => SDL.GpuShaderformatMsl,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such shader format."),
        };
    }

    extension(GpuBufferUsage usage)
    {
        /// <summary>
        /// Magic's usage bits are SDL's, in the same order.
        /// </summary>
        internal uint ToSdl() => (uint)usage;
    }

    extension(GpuTextureUsage usage)
    {
        /// <summary>
        /// Magic's usage bits are SDL's, in the same order.
        /// </summary>
        internal uint ToSdl() => (uint)usage;
    }

    extension(GpuTextureKind kind)
    {
        internal SDL.GPUTextureType ToSdl() => kind switch
        {
            GpuTextureKind.Texture2D => SDL.GPUTextureType.Type2d,
            GpuTextureKind.Texture2DArray => SDL.GPUTextureType.Type2dArray,
            GpuTextureKind.Texture3D => SDL.GPUTextureType.Type3d,
            GpuTextureKind.Cube => SDL.GPUTextureType.Cube,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such texture kind."),
        };
    }

    extension(GpuFilter filter)
    {
        internal SDL.GPUFilter ToSdl() => filter switch
        {
            GpuFilter.Linear => SDL.GPUFilter.Linear,
            GpuFilter.Nearest => SDL.GPUFilter.Nearest,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "There is no such filter."),
        };

        internal SDL.GPUSamplerMipmapMode ToSdlMipmaps() => filter switch
        {
            GpuFilter.Linear => SDL.GPUSamplerMipmapMode.Linear,
            GpuFilter.Nearest => SDL.GPUSamplerMipmapMode.Nearest,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "There is no such filter."),
        };
    }

    extension(GpuWrap wrap)
    {
        internal SDL.GPUSamplerAddressMode ToSdl() => wrap switch
        {
            GpuWrap.Clamp => SDL.GPUSamplerAddressMode.ClampToEdge,
            GpuWrap.Repeat => SDL.GPUSamplerAddressMode.Repeat,
            GpuWrap.Mirror => SDL.GPUSamplerAddressMode.MirroredRepeat,
            _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, "There is no such wrap."),
        };
    }

    extension(GpuCompare compare)
    {
        internal SDL.GPUCompareOp ToSdl() => compare switch
        {
            GpuCompare.Never => SDL.GPUCompareOp.Never,
            GpuCompare.Less => SDL.GPUCompareOp.Less,
            GpuCompare.Equal => SDL.GPUCompareOp.Equal,
            GpuCompare.LessOrEqual => SDL.GPUCompareOp.LessOrEqual,
            GpuCompare.Greater => SDL.GPUCompareOp.Greater,
            GpuCompare.NotEqual => SDL.GPUCompareOp.NotEqual,
            GpuCompare.GreaterOrEqual => SDL.GPUCompareOp.GreaterOrEqual,
            GpuCompare.Always => SDL.GPUCompareOp.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(compare), compare, "There is no such comparison."),
        };
    }

    extension(GpuVertexFormat format)
    {
        internal SDL.GPUVertexElementFormat ToSdl() => format switch
        {
            GpuVertexFormat.Float1 => SDL.GPUVertexElementFormat.Float,
            GpuVertexFormat.Float2 => SDL.GPUVertexElementFormat.Float2,
            GpuVertexFormat.Float3 => SDL.GPUVertexElementFormat.Float3,
            GpuVertexFormat.Float4 => SDL.GPUVertexElementFormat.Float4,
            GpuVertexFormat.Uint1 => SDL.GPUVertexElementFormat.Uint,
            GpuVertexFormat.Ubyte4Norm => SDL.GPUVertexElementFormat.Ubyte4Norm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such vertex format."),
        };
    }

    extension(GpuCull cull)
    {
        internal SDL.GPUCullMode ToSdl() => cull switch
        {
            GpuCull.None => SDL.GPUCullMode.None,
            GpuCull.Back => SDL.GPUCullMode.Back,
            GpuCull.Front => SDL.GPUCullMode.Front,
            _ => throw new ArgumentOutOfRangeException(nameof(cull), cull, "There is no such cull mode."),
        };
    }

    extension(GpuContents contents)
    {
        internal SDL.GPULoadOp ToSdl() => contents switch
        {
            GpuContents.Keep => SDL.GPULoadOp.Load,
            GpuContents.Clear => SDL.GPULoadOp.Clear,
            GpuContents.Discard => SDL.GPULoadOp.DontCare,
            _ => throw new ArgumentOutOfRangeException(nameof(contents), contents, "There is no such contents."),
        };
    }

    extension(GpuBlend blend)
    {
        internal SDL.GPUColorTargetBlendState ToSdl() => blend switch
        {
            GpuBlend.None => default,
            GpuBlend.Alpha => MakeBlending(SDL.GPUBlendFactor.SrcAlpha, SDL.GPUBlendFactor.OneMinusSrcAlpha, SDL.GPUBlendOp.Add),
            GpuBlend.Premultiplied => MakeBlending(SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.OneMinusSrcAlpha, SDL.GPUBlendOp.Add),
            GpuBlend.Add => MakeBlending(SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.One, SDL.GPUBlendOp.Add),
            GpuBlend.Max => MakeBlending(SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.One, SDL.GPUBlendOp.Max),
            _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "There is no such blend."),
        };
    }

    /// <summary>
    /// Colour blends <paramref name="source"/> and <paramref name="destination"/> with <paramref name="operation"/>;
    /// alpha does the same, except that straight alpha keeps its own: one of src, one minus src of dst.
    /// </summary>
    private static SDL.GPUColorTargetBlendState MakeBlending(SDL.GPUBlendFactor source, SDL.GPUBlendFactor destination, SDL.GPUBlendOp operation) => new()
    {
        EnableBlend = true,
        SrcColorBlendfactor = source,
        DstColorBlendfactor = destination,
        ColorBlendOp = operation,
        SrcAlphaBlendfactor = source == SDL.GPUBlendFactor.SrcAlpha ? SDL.GPUBlendFactor.One : source,
        DstAlphaBlendfactor = destination,
        AlphaBlendOp = operation,
    };
}
