using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Contexts.Assets;

/// <summary>
/// The one vertex layout every <see cref="Mesh"/> uses, so all geometry can share one vertex buffer and one pipeline
/// input layout. <see cref="Size"/> bytes, as the GPU reads them.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Vertex
{
    /// <summary>
    /// Bytes per vertex: what the shaders and the vertex buffer layout assume.
    /// </summary>
    public const int Size = 48;

    public Vector3 Position;

    public Vector3 Normal;

    /// <summary>
    /// The tangent in xyz, and in w the sign that turns <c>cross(Normal, Tangent.xyz)</c> into the bitangent, 1 or -1.
    /// </summary>
    public Vector4 Tangent;

    public Vector2 Uv;
}
