using Magic.Mathematics;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.Contexts.Assets;

/// <summary>
/// The one vertex layout every mesh uses, so all geometry can share a single vertex buffer and a single
/// pipeline input layout. Deviation flagged: a generic per-mesh layout was traded for this so the renderer
/// can be GPU driven (one mega buffer, one indirect draw per bucket). 48 bytes; <c>Tangent.W</c> is the
/// bitangent sign.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    /// <summary>
    /// Bytes per vertex; the shaders and the vertex buffer layout assume exactly this.
    /// </summary>
    public const int Size = 48;

    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Tangent;
    public Vector2 Uv;

#if DEBUG
    static Vertex()
    {
        Debug.Assert(Unsafe.SizeOf<Vertex>() == Size, $"{nameof(Vertex)} must be {Size} bytes.");
    }
#endif
}

/// <summary>
/// Geometry in engine space (left handed, +X right, +Y up, +Z forward, 1 unit = 1 metre = 1 Blender unit):
/// triangles wound clockwise seen from outside, tangents already generated, bounds computed. Filled by the
/// model's loader; not modified after it is published.
/// </summary>
public sealed class Mesh
{
    public Vertex[] Vertices { get; set; } = [];

    public uint[] Indices { get; set; } = [];

    /// <summary>
    /// The box around every vertex, in the mesh's own space.
    /// </summary>
    public Aabb Box { get; set; }

    /// <summary>
    /// The sphere around <see cref="Box"/>; what the renderer culls with. Radius 0 means not computed.
    /// </summary>
    public BoundingSphere Bounds { get; set; }

    /// <summary>
    /// Sets <see cref="Box"/> and <see cref="Bounds"/> from <see cref="Vertices"/>; the loader calls it once.
    /// </summary>
    public void ComputeBounds()
    {
        Box = BoxAround(Vertices);
        Bounds = Box.Sphere;
    }

    /// <summary>
    /// Over the positions of the vertices, without copying them out first.
    /// </summary>
    private static Aabb BoxAround(ReadOnlySpan<Vertex> vertices)
    {
        if (vertices.IsEmpty)
            return default;

        Vector3 min = vertices[0].Position, max = min;

        foreach (ref readonly Vertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return new Aabb(min, max);
    }
}
