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
    /// <summary>Bytes per vertex; the shaders and the vertex buffer layout assume exactly this.</summary>
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
/// triangles wound clockwise seen from
/// outside, tangents already generated. Filled by the model's loader; not modified after it is published.
/// </summary>
public sealed class Mesh
{
    public Vertex[] Vertices { get; set; } = [];

    public uint[] Indices { get; set; } = [];
}
