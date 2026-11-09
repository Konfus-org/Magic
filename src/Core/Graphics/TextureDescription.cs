using System.Numerics;

namespace Magic.Graphics;

/// <summary>
/// A texture's shape, which picks how a shader declares what reads it.
/// </summary>
public enum TextureKind : byte
{
    Texture2D,

    /// <summary>
    /// <see cref="TextureDescription.Layers"/> textures of one size, read as one with a layer index.
    /// </summary>
    Texture2DArray,

    /// <summary>
    /// Six square faces, read with a direction.
    /// </summary>
    Cube,

    /// <summary>
    /// <see cref="TextureDescription.Depth"/> slices, read with three coordinates.
    /// </summary>
    Texture3D,
}

/// <summary>
/// What a texture is for, which picks the memory layouts the GPU provider keeps it in.
/// </summary>
[Flags]
public enum TextureUsages : byte
{
    None = 0,

    /// <summary>
    /// Read by shaders through a sampler, or loaded texel by texel.
    /// </summary>
    Sampled = 1 << 0,

    /// <summary>
    /// Read and written by shaders as a storage image, a level at a time.
    /// </summary>
    Storage = 1 << 1,

    /// <summary>
    /// Drawn into by a draw pass as a colour target.
    /// </summary>
    ColorTarget = 1 << 2,

    /// <summary>
    /// Drawn into by a draw pass as the depth target.
    /// </summary>
    DepthTarget = 1 << 3,

    /// <summary>
    /// Copied from, into a buffer or another texture.
    /// </summary>
    CopySource = 1 << 4,

    /// <summary>
    /// Copied into, from a buffer or another texture.
    /// </summary>
    CopyDestination = 1 << 5,
}

/// <summary>
/// How to make a texture: its kind, size, levels and format, and what it is for.
/// </summary>
public readonly record struct TextureDescription
{
    public TextureDescription() { }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required GpuFormat Format { get; init; }

    public required TextureUsages Usage { get; init; }

    public TextureKind Kind { get; init; } = TextureKind.Texture2D;

    /// <summary>
    /// Slices of a <see cref="TextureKind.Texture3D"/>; 1 for the rest.
    /// </summary>
    public int Depth { get; init; } = 1;

    /// <summary>
    /// Layers of a <see cref="TextureKind.Texture2DArray"/>; 6 for a cube, 1 for the rest.
    /// </summary>
    public int Layers { get; init; } = 1;

    /// <summary>
    /// Mip levels, level 0 the full size and each the half of the one before.
    /// </summary>
    public int Levels { get; init; } = 1;

    /// <summary>
    /// What a GPU debugger shows it as.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// How many levels a texture of <paramref name="width"/> by <paramref name="height"/> has down to one texel.
    /// </summary>
    public static int GetFullLevels(int width, int height) => 1 + BitOperations.Log2((uint)Math.Max(width, height));
}
