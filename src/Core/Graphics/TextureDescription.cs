using System.Numerics;

namespace Magic.Graphics;

/// <summary>
/// A texture's shape, which picks how a shader declares what reads it.
/// </summary>
public enum TextureKind : byte
{
    Texture2D,

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
/// How to make a texture: the name a GPU debugger shows it as, its kind, size, levels and format, and what it is for.
/// Level 0 is the full size, and each level is half the size of the level before it.
/// </summary>
public readonly record struct TextureDescription(
    string Name,
    int Width,
    int Height,
    GpuFormat Format,
    TextureUsages Usage = TextureUsages.Storage | TextureUsages.Sampled,
    int Levels = 1
)
{
    public TextureKind Kind { get; init; } = TextureKind.Texture2D;

    /// <summary>
    /// Slices of a <see cref="TextureKind.Texture3D"/>; 1 for the rest.
    /// </summary>
    public int Depth { get; init; } = 1;

    /// <summary>
    /// How many levels a texture of <paramref name="width"/> by <paramref name="height"/> has down to one texel.
    /// </summary>
    public static int GetFullLevels(int width, int height) => 1 + BitOperations.Log2((uint)Math.Max(width, height));
}
