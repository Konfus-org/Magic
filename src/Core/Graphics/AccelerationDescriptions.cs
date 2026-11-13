using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Graphics;

/// <summary>
/// Triangles of the heap's buffers for a bottom-level acceleration structure: positions as three floats at a stride
/// in a vertex buffer, and the triangles as 32-bit indices into it.
/// </summary>
public readonly record struct TriangleGeometry(
    GpuBuffer Vertices,
    ulong VertexOffset,
    uint VertexStride,
    uint VertexCount,
    GpuBuffer Indices,
    ulong IndexOffset,
    uint TriangleCount
);

/// <summary>
/// A bottom-level acceleration structure: a model's triangles, in geometries a ray reports the index of when it
/// lands on one.
/// </summary>
public readonly record struct BottomLevelDescription
{
    public BottomLevelDescription() { }

    public required ImmutableArray<TriangleGeometry> Geometries { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// A top-level acceleration structure: up to <see cref="MaxInstances"/> instances of bottom-level ones, read from
/// an <see cref="BufferUsages.Storage"/> buffer of <see cref="GpuAccelerationInstance"/> rows when built.
/// </summary>
public readonly record struct TopLevelDescription
{
    public TopLevelDescription() { }

    public required int MaxInstances { get; init; }

    public required GpuBuffer Instances { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// An instance of a bottom-level acceleration structure as the GPU reads it, 64 bytes.
/// </summary>
/// <remarks>
/// The fields are the world transform's top three rows for column vectors, a 24-bit index with an 8-bit mask above
/// it, a 24-bit offset with 8 bits of flags above it, and the bottom-level structure's address.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuAccelerationInstance
{
    public const int Bytes = 64;

    /// <summary>
    /// Lets a ray hit the instance's triangles from the front and from the back.
    /// </summary>
    public const uint FlagNoFaceCull = 1;

    /// <summary>
    /// Lets a ray query that does not make all hits opaque decide for each hit triangle whether it stops the ray, as
    /// for glass.
    /// </summary>
    public const uint FlagNoOpaque = 8;

    public Vector4 Row0;

    public Vector4 Row1;

    public Vector4 Row2;

    public uint CustomIndexAndMask;

    public uint OffsetAndFlags;

    public ulong Reference;
}
