using System.Numerics;

namespace Magic.Contexts.Assets;

/// <summary>
/// How a vertex says it is a corner of an impostor's card (a model's last generated LOD, or a far chunk's stand-in for
/// a small object), and which model's impostor it shows: the generators write it, the renderer reads it. A card's
/// vertex has its normal's x and y as its corner (-1 or 1), its normal's z as half the card's side, its position as that
/// corner of the square facing +Z, its tangent's z as the card's turn about the up axis (radians; a stand-in's merged
/// cards are each turned as their object was), its tangent's w as <see cref="Tag"/> plus the index of the model's mesh
/// it shows, and the model's id in four 16-bit pieces, exact in a float each, in its tangent's x and y and its uv. The
/// renderer looks the model's impostor up by that id (its <see cref="Lod.Atlases"/>, two per mesh) and writes the
/// atlases' references over the tangent's x and y, which is where the shaders read them (Include/Impostor.hlsli).
/// </summary>
public static class ImpostorCard
{
    /// <summary>
    /// What a card vertex's tangent w starts at; a mesh vertex's is the bitangent's sign, +1 or -1.
    /// </summary>
    public const float Tag = 4f;

    /// <summary>
    /// A card's four corners, in the order its <see cref="Indices"/> name them.
    /// </summary>
    public static readonly Vector2[] Corners = [new(-1f, -1f), new(-1f, 1f), new(1f, 1f), new(1f, -1f)];

    /// <summary>
    /// A card's two triangles. Cards are drawn two-sided, so their winding does not matter.
    /// </summary>
    public static readonly uint[] Indices = [0, 1, 2, 0, 2, 3];

    public static bool IsCard(in Vertex vertex)
    {
        return vertex.Tangent.W > Tag * 0.5f;
    }

    /// <summary>
    /// A card's corner: <paramref name="corner"/> is (-1 or 1, -1 or 1), <paramref name="center"/> the card's middle and
    /// <paramref name="half"/> half its side, both where the card is drawn; <paramref name="yaw"/> its turn about the up axis.
    /// </summary>
    public static Vertex Corner(Vector2 corner, Vector3 center, float half, float yaw, ulong model, int mesh)
    {
        return new Vertex
        {
            Position = center + new Vector3(corner * half, 0f),
            Normal = new Vector3(corner, half),
            Tangent = new Vector4(model & 0xFFFF, (model >> 16) & 0xFFFF, yaw, Tag + mesh),
            Uv = new Vector2((model >> 32) & 0xFFFF, (model >> 48) & 0xFFFF),
        };
    }

    /// <summary>
    /// The model and the mesh of it a card shows.
    /// </summary>
    public static (ulong Model, int Mesh) Source(in Vertex vertex)
    {
        ulong model = (ulong)vertex.Tangent.X | ((ulong)vertex.Tangent.Y << 16) | ((ulong)vertex.Uv.X << 32) | ((ulong)vertex.Uv.Y << 48);
        return (model, (int)(vertex.Tangent.W - Tag + 0.5f));
    }

    /// <summary>
    /// A card's middle and half side, from one of its corners.
    /// </summary>
    public static (Vector3 Center, float Half) Square(in Vertex vertex)
    {
        float half = vertex.Normal.Z;
        return (vertex.Position - new Vector3(vertex.Normal.X * half, vertex.Normal.Y * half, 0f), half);
    }
}
