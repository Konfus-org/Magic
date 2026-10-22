using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// One mip level of one layer of a texture, or a box of it: what
/// <see cref="IGpu.Upload(in GpuTextureRegion, ReadOnlySpan{byte})"/> fills and <see cref="GpuCommands.CopyTexture"/>
/// copies. A width, height or depth of 0 is the whole level; <see cref="Z"/> and <see cref="Depth"/> are the slices of
/// a 3D texture.
/// </summary>
public readonly record struct GpuTextureRegion(
    GpuTexture Texture,
    uint Level = 0,
    uint Layer = 0,
    uint X = 0,
    uint Y = 0,
    uint Z = 0,
    uint Width = 0,
    uint Height = 0,
    uint Depth = 0);
