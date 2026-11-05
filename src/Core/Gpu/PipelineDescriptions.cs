using System.Collections.Immutable;

namespace Magic.Gpu;

/// <summary>
/// Which faces of a triangle a draw skips. Triangles are clockwise from outside (<see cref="Files.Types.Model"/>).
/// </summary>
public enum CullMode : byte
{
    None,
    Back,
    Front,
}

/// <summary>
/// How a draw's depth is tested and written. Depth is reversed: 1 at the near plane and 0 at infinity, so the test
/// passes what is nearer as greater.
/// </summary>
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

    /// <summary>
    /// Nearer or as near passes, and the depth is left as it is.
    /// </summary>
    TestOnly,
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
/// How to build a compute pipeline: one entry's SPIR-V.
/// </summary>
public readonly record struct ComputePipelineDescription
{
    public ComputePipelineDescription() { }

    public required ReadOnlyMemory<byte> Spirv { get; init; }

    public required string EntryPoint { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// How to build a graphics pipeline: a vertex and a pixel entry, the formats of the targets it draws into, and its
/// fixed state. It takes no vertex layout: vertices are fetched from the heap by the vertex entry. Viewport and scissor
/// are commands.
/// </summary>
public readonly record struct GraphicsPipelineDescription
{
    public GraphicsPipelineDescription() { }

    public required ReadOnlyMemory<byte> VertexSpirv { get; init; }

    public required string VertexEntryPoint { get; init; }

    public required ReadOnlyMemory<byte> PixelSpirv { get; init; }

    public required string PixelEntryPoint { get; init; }

    /// <summary>
    /// The colour targets it draws into, in order; none for a depth-only draw.
    /// </summary>
    public ImmutableArray<GpuFormat> ColorFormats { get; init; } = [];

    /// <summary>
    /// The depth target it draws into; <see cref="GpuFormat.Unknown"/> for none.
    /// </summary>
    public GpuFormat DepthFormat { get; init; }

    public DepthMode Depth { get; init; }

    public BlendMode Blend { get; init; }

    public CullMode Cull { get; init; } = CullMode.Back;

    public string Name { get; init; } = string.Empty;
}
