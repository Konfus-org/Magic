using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A texture's shape.
/// </summary>
public enum GpuTextureKind : byte
{
    Texture2D,
    Texture2DArray,
    Texture3D,

    /// <summary>
    /// Six square faces in the order +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    Cube,
}

/// <summary>
/// A texture for <see cref="IGpu.CreateTexture"/> to make. <paramref name="Layers"/> is the layer count of a
/// <see cref="GpuTextureKind.Texture2DArray"/>, the depth of a <see cref="GpuTextureKind.Texture3D"/>, and 6 for a
/// <see cref="GpuTextureKind.Cube"/>.
/// </summary>
public readonly record struct GpuTextureDescription(
    GpuFormat Format,
    GpuTextureUsage Usage,
    uint Width,
    uint Height,
    uint Levels = 1,
    uint Layers = 1,
    GpuTextureKind Kind = GpuTextureKind.Texture2D);
