using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Magic.Gpu;

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
/// An instance of a bottom-level acceleration structure as the GPU reads it, 64 bytes: the world transform's top
/// three rows for column vectors, a 24-bit index with an 8-bit mask above it, a 24-bit offset with 8 bits of flags
/// above it, and the bottom-level structure's address.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuAccelerationInstance
{
    public const int Bytes = 64;

    /// <summary>
    /// A ray landing on the instance's triangles sees it facing either way.
    /// </summary>
    public const uint FlagNoFaceCull = 1;

    public float M00;

    public float M01;

    public float M02;

    public float M03;

    public float M10;

    public float M11;

    public float M12;

    public float M13;

    public float M20;

    public float M21;

    public float M22;

    public float M23;

    public uint CustomIndexAndMask;

    public uint OffsetAndFlags;

    public ulong Reference;
}
