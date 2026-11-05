using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Rendering;

/// <summary>
/// A vertex as the GPU reads it from the vertex pool, 32 bytes: the twin of <c>Scene/Vertices.slang</c>'s. Normals and
/// tangents are octahedral, packed as <c>Core/Pack.slang</c> packs them.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuVertex
{
    public const int Bytes = 32;

    public Vector3 Position;

    /// <summary>
    /// The unit normal, octahedral at 16 bits an axis.
    /// </summary>
    public uint Normal;

    /// <summary>
    /// The unit tangent, octahedral at 16 bits an axis.
    /// </summary>
    public uint Tangent;

    /// <summary>
    /// 1 or -1: what turns the cross of the normal and the tangent into the bitangent.
    /// </summary>
    public float TangentSign;

    public Vector2 Uv;
}
