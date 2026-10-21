namespace Magic.Contexts.Assets;

/// <summary>
/// A card: a flat stand-in for a mesh far away, a quad that turns about its vertical to face the camera and shows the
/// mesh as it was seen from the nearest of its <see cref="Views"/>, shaded with the mesh's own material. A
/// <see cref="Mesh"/> that is a card holds one (<see cref="Mesh.Card"/>), and its vertices say where the quad is, four
/// to a card: each <see cref="Vertex.Position"/> the card's centre, <see cref="Vertex.Uv"/> its corner (-1 or 1 on each
/// axis), and <see cref="Vertex.Normal"/> its half width, its half height and its turn about +Y in radians, so many
/// cards can be one mesh.
/// <para>
/// View n is the mesh seen from n of <see cref="Views"/> equal turns about +Y from its front, +Z, looking back at it:
/// the cell of a <see cref="Columns"/>-wide grid, read left to right then top to bottom, in each of two atlases.
/// </para>
/// </summary>
public sealed class Card
{
    /// <summary>
    /// The views each atlas holds, the mesh seen from each of as many equal turns about +Y.
    /// </summary>
    public const int Views = 8;

    /// <summary>
    /// The views a row of each atlas holds; the rows are as tall as the views are wide.
    /// </summary>
    public const int Columns = 3;

    /// <summary>
    /// Texels a side of each atlas; each view's cell is <c>Size / Columns</c> texels a side, from the top left.
    /// </summary>
    public const int Size = 256;

    /// <summary>
    /// The views as the mesh's material sees them: the uv the material is read at in red and green, how far behind the
    /// card's middle the mesh is there in blue (-1 to 1 of half its side as 0 to 1), so the card is drawn at the mesh's
    /// own depth, and how much of the texel the mesh covers in alpha.
    /// </summary>
    public required Texture Surface { get; init; }

    /// <summary>
    /// The views' normals, in the mesh's own space, as xyz * 0.5 + 0.5, and the mesh's cover in alpha.
    /// </summary>
    public required Texture Normals { get; init; }
}
