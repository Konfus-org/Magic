using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;

namespace Magic.ClusterRendering;

/// <summary>
/// What a name a pass's shader reads or writes stands for: a texture or a buffer, the renderer's or one a pass makes,
/// for the frame, for each view or for each target (<see cref="IsTarget"/>), how a shader declares it, and how a texture
/// is sampled wherever one samples it. The renderer's own are <see cref="GpuContract"/>'s, which also say how the
/// renderer uses what it makes for them; a pass's come from its <see cref="PassCreate"/>.
/// </summary>
[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "A format or shape maps to the HLSL it is declared as; the rest share a default.")]
internal sealed record ResourceInfo(string Name, bool IsBuffer, bool IsFrame, GpuFormat Format, GpuTextureKind Shape, string BufferType, PassFilter Filter = PassFilter.Default, bool IsTarget = false, GpuBufferUsage BufferUsage = GpuBufferUsage.None, GpuTextureUsage TextureUsage = GpuTextureUsage.None)
{
    /// <summary>
    /// What ends the name a texture kept from frame to frame is read by as it was the frame before
    /// (<see cref="GetHistoryName"/>).
    /// </summary>
    internal const string HistorySuffix = "History";

    internal bool IsDepth => Format == GpuFormat.D32Float;

    /// <summary>
    /// A texture whose texels cannot be filtered, an integer's, is read as a storage texture, texel by texel with no
    /// sampler, as no GPU samples it.
    /// </summary>
    internal bool IsStorageRead => !IsBuffer && Format == GpuFormat.R32Uint;

    /// <summary>
    /// The HLSL type a shader reads it as: <c>Texture2D&lt;float4&gt;</c>, or for a buffer <c>ByteAddressBuffer</c>,
    /// whose elements of <see cref="BufferType"/> it reads with LOAD (Include/Bindings.hlsli).
    /// </summary>
    internal string ReadType => IsBuffer ? "ByteAddressBuffer" : $"{TextureName}<{Element}>";

    /// <summary>
    /// The HLSL type a compute shader writes it as, with the format Vulkan needs to write a storage texture.
    /// </summary>
    internal string WriteType => IsBuffer ? "RWByteAddressBuffer" : $"[[vk::image_format(\"{StorageFormat}\")]] RW{(Shape == GpuTextureKind.Cube ? "Texture2DArray" : TextureName)}<{Element}>";

    private string TextureName => Shape switch
    {
        GpuTextureKind.Texture2DArray => "Texture2DArray",
        GpuTextureKind.Texture3D => "Texture3D",
        GpuTextureKind.Cube => "TextureCube",
        _ => "Texture2D",
    };

    private string Element => Format switch
    {
        GpuFormat.D32Float or GpuFormat.R32Float or GpuFormat.R16Float or GpuFormat.R8Unorm => "float",
        GpuFormat.Rg16Float or GpuFormat.Rg32Float or GpuFormat.Rg8Unorm => "float2",
        GpuFormat.R32Uint => "uint",
        _ => "float4",
    };

    private string StorageFormat => Format switch
    {
        GpuFormat.Rgba16Float => "rgba16f",
        GpuFormat.Rgba32Float => "rgba32f",
        GpuFormat.Rgba8Unorm => "rgba8",
        GpuFormat.Rgb10A2Unorm => "rgb10_a2",
        GpuFormat.R11G11B10Float => "r11g11b10f",
        GpuFormat.Rg16Float => "rg16f",
        GpuFormat.Rg32Float => "rg32f",
        GpuFormat.R16Float => "r16f",
        GpuFormat.R32Float => "r32f",
        GpuFormat.R32Uint => "r32ui",
        GpuFormat.R8Unorm => "r8",
        GpuFormat.Rg8Unorm => "rg8",
        _ => "unknown",
    };

    /// <summary>
    /// The name a texture kept from frame to frame (<see cref="PassCreate.History"/>) is read by as it was the frame
    /// before.
    /// </summary>
    internal static string GetHistoryName(string name) => name + HistorySuffix;

    /// <summary>
    /// What a pass makes, as it declares it: for the frame when it is made in a stage that runs once a frame.
    /// </summary>
    internal static ResourceInfo Of(PassCreate create, bool isFrame, bool isTarget) => new(
        create.Name,
        create.Kind == PassResource.Buffer,
        isFrame,
        create.Format,
        create.Cube ? GpuTextureKind.Cube : create.Depth > 0 ? GpuTextureKind.Texture3D : create.Layers > 1 ? GpuTextureKind.Texture2DArray : GpuTextureKind.Texture2D,
        create.Type,
        create.Filter,
        isTarget);
}

/// <summary>
/// A name as it stands this frame: its texture or buffer, its size, and for a texture that is read through one sampler
/// wherever it is read, the materials' textures, that sampler.
/// </summary>
internal readonly record struct BoundResource(GpuTexture Texture, GpuBuffer Buffer, uint Width, uint Height, uint Bytes, GpuSampler Sampler = default)
{
    internal static BoundResource Of(GpuTexture texture, uint width, uint height, GpuSampler sampler = default) => new(texture, GpuBuffer.None, width, height, 0, sampler);

    internal static BoundResource Of(GpuBuffer buffer, uint bytes) => new(GpuTexture.None, buffer, 0, 0, bytes);
}

/// <summary>
/// Everything that sizes and shapes what a pass makes: two made from the same key are alike, so a key that changes, a
/// view resized or a count grown, makes it again. <see cref="HasHistory"/> makes it twice, the frame's and the last
/// frame's (<see cref="ResourceScope.Swap"/>).
/// </summary>
internal readonly record struct ResourceKey(
    bool IsBuffer,
    GpuFormat Format,
    GpuTextureKind Shape,
    GpuTextureUsage TextureUsage,
    GpuBufferUsage BufferUsage,
    uint Width,
    uint Height,
    uint Layers,
    uint Levels,
    uint Bytes,
    bool HasHistory = false);
