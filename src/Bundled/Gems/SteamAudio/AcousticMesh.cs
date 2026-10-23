using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.SteamAudio;

/// <summary>
/// A model's triangles in Steam Audio's space, in the model's own, unscaled: every mesh's vertices mirrored along Z,
/// and each triangle's winding turned with them, so its front stays the side the model shows. Made whole on a worker,
/// so whoever has one reads it.
/// </summary>
internal sealed class AcousticMesh
{
    private AcousticMesh(IPLVector3[] vertices, IPLTriangle[] triangles, float reach)
    {
        Vertices = vertices;
        Triangles = triangles;
        Materials = new int[triangles.Length];
        Reach = reach;
    }

    internal IPLVector3[] Vertices { get; }

    internal IPLTriangle[] Triangles { get; }

    /// <summary>
    /// Each triangle's material: all 0, the one material of its sub-scene.
    /// </summary>
    internal int[] Materials { get; }

    /// <summary>
    /// How far the model reaches from its origin at scale 1, at most.
    /// </summary>
    internal float Reach { get; }

    /// <summary>
    /// The triangles of every mesh of <paramref name="model"/>; null for a model with none.
    /// </summary>
    internal static unsafe AcousticMesh? Of(Model model)
    {
        int vertexCount = 0;
        int triangleCount = 0;
        foreach (Mesh mesh in model.Meshes)
        {
            vertexCount += mesh.Vertices.Length;
            triangleCount += mesh.Indices.Length / 3;
        }

        if (vertexCount == 0 || triangleCount == 0) return null;

        // Every mesh's vertices mirrored into the one array, the farthest from the origin kept as the reach, and its
        // triangles offset to where its vertices start, their last two corners swapped as the mirror turns the winding.
        IPLVector3[] vertices = new IPLVector3[vertexCount];
        IPLTriangle[] triangles = new IPLTriangle[triangleCount];
        float reach = 0;
        int vertexAt = 0;
        int triangleAt = 0;
        foreach (Mesh mesh in model.Meshes)
        {
            ReadOnlySpan<Vertex> meshVertices = mesh.Vertices.Span;
            for (int i = 0; i < meshVertices.Length; i++)
            {
                Vector3 position = meshVertices[i].Position;
                reach = Math.Max(reach, position.Length());
                vertices[vertexAt + i] = SoundWorld.ToVector(position);
            }

            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                ref IPLTriangle triangle = ref triangles[triangleAt++];
                triangle.indices[0] = vertexAt + (int)indices[i];
                triangle.indices[1] = vertexAt + (int)indices[i + 2];
                triangle.indices[2] = vertexAt + (int)indices[i + 1];
            }

            vertexAt += meshVertices.Length;
        }

        return new AcousticMesh(vertices, triangles, Math.Max(reach, 0.01f));
    }

    /// <summary>
    /// A cube a metre across, centred on its origin: what an entity with no model is, as its body is.
    /// </summary>
    internal static unsafe AcousticMesh CreateCube()
    {
        // Corner i's bits 1, 2 and 4 put it at the high side of x, y and z.
        IPLVector3[] vertices = new IPLVector3[8];
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            vertices[i] = SoundWorld.ToVector(corner);
        }

        // Each face's corners in the engine's space, (x, y, z) being corner x + 2y + 4z, clockwise seen from outside as
        // the engine winds its models; each triangle is turned as a model's are once mirrored.
        ReadOnlySpan<int> faces = [0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3, 0, 2, 3, 1, 4, 5, 7, 6];
        IPLTriangle[] triangles = new IPLTriangle[12];
        for (int face = 0; face < 6; face++)
        {
            ReadOnlySpan<int> corners = faces.Slice(face * 4, 4);
            ref IPLTriangle first = ref triangles[face * 2];
            first.indices[0] = corners[0];
            first.indices[1] = corners[2];
            first.indices[2] = corners[1];
            ref IPLTriangle second = ref triangles[(face * 2) + 1];
            second.indices[0] = corners[0];
            second.indices[1] = corners[3];
            second.indices[2] = corners[2];
        }

        return new AcousticMesh(vertices, triangles, MathF.Sqrt(0.75f));
    }
}
