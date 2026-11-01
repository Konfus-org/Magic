using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Assets.Types;

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

/// <summary>
/// One part of a <see cref="Model"/>: triangles drawn with one material.
/// </summary>
public sealed class Mesh
{
    public required ReadOnlyMemory<Vertex> Vertices { get; init; }

    /// <summary>
    /// Three per triangle, into <see cref="Vertices"/>.
    /// </summary>
    public required ReadOnlyMemory<uint> Indices { get; init; }

    /// <summary>
    /// Which of the material slots of whatever draws it it is drawn with, 0 to 7.
    /// </summary>
    public int MaterialSlot { get; init; }
}

/// <summary>
/// The meshes imported from one file. They are in the engine's frame (+X right, +Y up, +Z forward, a metre a unit,
/// triangles clockwise from outside), with <see cref="Origin"/> moved to (0, 0, 0).
/// </summary>
public sealed class Model : IAsset<Model>
{
    public required ImmutableArray<Mesh> Meshes { get; init; }

    /// <summary>
    /// The point of the model, in the engine's frame and metres, moved to (0, 0, 0): what sits at its entity's
    /// position, and what scale and rotation turn about.
    /// </summary>
    [AssetMeta]
    public Vector3 Origin { get; set; }
}
