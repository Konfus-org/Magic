using Magic.Contexts.Rendering;
using SDL3;

namespace SDLRenderGem;

/// <summary>The engine's GPU enums to SDL's, and the one way back (a swapchain's format).</summary>
internal static class FormatExtensions
{
    extension(GpuFormat format)
    {
        public SDL.GPUTextureFormat ToSdl()
        {
            return format switch
            {
                GpuFormat.Rgba8Unorm => SDL.GPUTextureFormat.R8G8B8A8Unorm,
                GpuFormat.Rgba8Srgb => SDL.GPUTextureFormat.R8G8B8A8UnormSRGB,
                GpuFormat.Bgra8Unorm => SDL.GPUTextureFormat.B8G8R8A8Unorm,
                GpuFormat.Bgra8Srgb => SDL.GPUTextureFormat.B8G8R8A8UnormSRGB,
                GpuFormat.Rgba16Float => SDL.GPUTextureFormat.R16G16B16A16Float,
                GpuFormat.R16Float => SDL.GPUTextureFormat.R16Float,
                GpuFormat.R32Float => SDL.GPUTextureFormat.R32Float,
                GpuFormat.Rg16Float => SDL.GPUTextureFormat.R16G16Float,
                GpuFormat.R11G11B10Float => SDL.GPUTextureFormat.R11G11B10UFloat,
                GpuFormat.Rgb10A2Unorm => SDL.GPUTextureFormat.R10G10B10A2Unorm,
                GpuFormat.D32Float => SDL.GPUTextureFormat.D32Float,
                GpuFormat.D24Unorm => SDL.GPUTextureFormat.D24Unorm,
                _ => SDL.GPUTextureFormat.Invalid,
            };
        }
    }

    extension(SDL.GPUTextureFormat format)
    {
        public GpuFormat ToEngine()
        {
            return format switch
            {
                SDL.GPUTextureFormat.R8G8B8A8Unorm => GpuFormat.Rgba8Unorm,
                SDL.GPUTextureFormat.R8G8B8A8UnormSRGB => GpuFormat.Rgba8Srgb,
                SDL.GPUTextureFormat.B8G8R8A8Unorm => GpuFormat.Bgra8Unorm,
                SDL.GPUTextureFormat.B8G8R8A8UnormSRGB => GpuFormat.Bgra8Srgb,
                SDL.GPUTextureFormat.R16G16B16A16Float => GpuFormat.Rgba16Float,
                SDL.GPUTextureFormat.R10G10B10A2Unorm => GpuFormat.Rgb10A2Unorm,
                SDL.GPUTextureFormat.D32Float => GpuFormat.D32Float,
                SDL.GPUTextureFormat.D24Unorm => GpuFormat.D24Unorm,
                _ => GpuFormat.Invalid,
            };
        }
    }

    extension(GpuBufferUsage usage)
    {
        public SDL.GPUBufferUsageFlags ToSdl()
        {
            SDL.GPUBufferUsageFlags flags = 0;
            if (usage.HasFlag(GpuBufferUsage.Vertex)) flags |= SDL.GPUBufferUsageFlags.Vertex;
            if (usage.HasFlag(GpuBufferUsage.Index)) flags |= SDL.GPUBufferUsageFlags.Index;
            if (usage.HasFlag(GpuBufferUsage.Indirect)) flags |= SDL.GPUBufferUsageFlags.Indirect;
            if (usage.HasFlag(GpuBufferUsage.GraphicsRead)) flags |= SDL.GPUBufferUsageFlags.GraphicsStorageRead;
            if (usage.HasFlag(GpuBufferUsage.ComputeRead)) flags |= SDL.GPUBufferUsageFlags.ComputeStorageRead;
            if (usage.HasFlag(GpuBufferUsage.ComputeWrite)) flags |= SDL.GPUBufferUsageFlags.ComputeStorageWrite;
            return flags;
        }
    }

    extension(GpuTextureUsage usage)
    {
        public SDL.GPUTextureUsageFlags ToSdl()
        {
            SDL.GPUTextureUsageFlags flags = 0;
            if (usage.HasFlag(GpuTextureUsage.Sampler)) flags |= SDL.GPUTextureUsageFlags.Sampler;
            if (usage.HasFlag(GpuTextureUsage.ColorTarget)) flags |= SDL.GPUTextureUsageFlags.ColorTarget;
            if (usage.HasFlag(GpuTextureUsage.DepthTarget)) flags |= SDL.GPUTextureUsageFlags.DepthStencilTarget;
            if (usage.HasFlag(GpuTextureUsage.ComputeWrite)) flags |= SDL.GPUTextureUsageFlags.ComputeStorageWrite;
            return flags;
        }
    }

    extension(GpuVertexFormat format)
    {
        public SDL.GPUVertexElementFormat ToSdl()
        {
            return format switch
            {
                GpuVertexFormat.Float2 => SDL.GPUVertexElementFormat.Float2,
                GpuVertexFormat.Float3 => SDL.GPUVertexElementFormat.Float3,
                GpuVertexFormat.Float4 => SDL.GPUVertexElementFormat.Float4,
                GpuVertexFormat.Uint => SDL.GPUVertexElementFormat.Uint,
                _ => SDL.GPUVertexElementFormat.Ubyte4Norm,
            };
        }
    }

    extension(GpuCull cull)
    {
        public SDL.GPUCullMode ToSdl()
        {
            return cull switch
            {
                GpuCull.Back => SDL.GPUCullMode.Back,
                GpuCull.Front => SDL.GPUCullMode.Front,
                _ => SDL.GPUCullMode.None,
            };
        }
    }

    extension(GpuCompare compare)
    {
        public SDL.GPUCompareOp ToSdl()
        {
            return compare switch
            {
                GpuCompare.Less => SDL.GPUCompareOp.Less,
                GpuCompare.LessOrEqual => SDL.GPUCompareOp.LessOrEqual,
                GpuCompare.Greater => SDL.GPUCompareOp.Greater,
                GpuCompare.GreaterOrEqual => SDL.GPUCompareOp.GreaterOrEqual,
                _ => SDL.GPUCompareOp.Always,
            };
        }
    }

    extension(GpuLoad load)
    {
        public SDL.GPULoadOp ToSdl()
        {
            return load switch
            {
                GpuLoad.Clear => SDL.GPULoadOp.Clear,
                GpuLoad.DontCare => SDL.GPULoadOp.DontCare,
                _ => SDL.GPULoadOp.Load,
            };
        }
    }
}
