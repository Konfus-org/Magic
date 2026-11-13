namespace Magic.Graphics;

/// <summary>
/// Which faces of a triangle a draw skips. Triangles are clockwise from outside (<see cref="Assets.Model"/>).
/// </summary>
public enum CullMode : byte
{
    None,
    Back,
    Front,
}

/// <summary>
/// How a draw's depth is tested and written.
/// </summary>
/// <remarks>
/// Depth is reversed: 1 at the near plane and 0 at infinity, so a nearer value is greater.
/// </remarks>
public enum DepthMode : byte
{
    /// <summary>
    /// No depth target, or one left alone.
    /// </summary>
    None,

    /// <summary>
    /// Nearer or as near passes, and the depth is written.
    /// </summary>
    TestAndWrite,
}

/// <summary>
/// How a draw's colour lands on what the target holds.
/// </summary>
public enum BlendMode : byte
{
    /// <summary>
    /// Replaces it.
    /// </summary>
    Opaque,

    /// <summary>
    /// Blends over it by the colour's alpha, the colour premultiplied.
    /// </summary>
    Alpha,

    /// <summary>
    /// Adds to it.
    /// </summary>
    Additive,

    /// <summary>
    /// Multiplies it, as a transmittance does.
    /// </summary>
    Multiply,
}

/// <summary>
/// A draw pass's shaders and fixed state, which the GPU provider builds into a pipeline for the pass's target formats.
/// </summary>
public readonly record struct DrawShaders(ShaderEntry Vertex, ShaderEntry Pixel)
{
    public DepthMode Depth { get; init; }

    public BlendMode Blend { get; init; }

    public CullMode Cull { get; init; } = CullMode.Back;
}
