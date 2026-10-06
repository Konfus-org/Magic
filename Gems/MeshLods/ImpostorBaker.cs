using Magic.Contexts.Assets;
using System.Numerics;

namespace MeshLods;

/// <summary>
/// Bakes a model into an impostor: the model as seen from <see cref="Frames"/> directions around its up axis (+Y), every
/// 45 degrees, each into one cell of a <see cref="Grid"/> by <see cref="Grid"/> atlas, on the CPU (an orthographic
/// rasteriser with a depth buffer, <see cref="Samples"/> by <see cref="Samples"/> samples a texel). Frame k looks from
/// (sin k45, 0, cos k45) towards the model; its image's right is (-cos k45, 0, sin k45) and its up is +Y. Every frame
/// covers the same square, 2 <c>h</c> a side, centred on the model's box, so the card that shows it is the same for all.
/// <para>
/// Each mesh of the model gets two atlases of the texels it is the nearest surface in, so the meshes' cards, all in one
/// place, cover disjoint pixels and each is drawn with its own material: the first holds the surface's uv (R, G) and
/// how much of the texel it covers (B); the second its normal in the model's space (R, G, B, from -1..1). Empty
/// texels take their neighbours' uv and normal (coverage stays 0), so filtering at the silhouette blends towards the
/// surface rather than towards nothing. The card is a quad whose vertices say it is one, and of which model's mesh
/// (<see cref="ImpostorCard"/>), from which the renderer finds the atlases. Twin of Include/Impostor.hlsli.
/// </para>
/// </summary>
internal static class ImpostorBaker
{
    public const int Frames = 8;

    public const int Grid = 3;

    public const int AtlasSize = 256;

    public const int FrameSize = AtlasSize / Grid;

    private const int Samples = 2;

    private const int Dilations = 4;

    /// <summary>
    /// Per mesh of the model, its card and its two atlases as RGBA rows, <see cref="AtlasSize"/> square.
    /// </summary>
    public static (Mesh Card, byte[] Surface, byte[] Normal)[] Bake(Model model, ulong id)
    {
        (Vector3 center, float half) = Square(model);
        int meshes = model.Meshes.Length;
        byte[][] surfaces = [.. Enumerable.Range(0, meshes).Select(_ => new byte[AtlasSize * AtlasSize * 4])];
        byte[][] normals = [.. Enumerable.Range(0, meshes).Select(_ => new byte[AtlasSize * AtlasSize * 4])];
        for (int frame = 0; frame < Frames; frame++)
            BakeFrame(model, frame, center, half, surfaces, normals);

        for (int mesh = 0; mesh < meshes; mesh++)
            Dilate(surfaces[mesh], normals[mesh]);

        return [.. Enumerable.Range(0, meshes).Select(mesh => (Card(center, half, id, mesh), surfaces[mesh], normals[mesh]))];
    }

    /// <summary>
    /// The middle of the model's box, and half the side of the square every frame shows: enough for the model's height
    /// and for its widest reach from the up axis through the middle, whichever way it is seen from.
    /// </summary>
    private static (Vector3 Center, float Half) Square(Model model)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Mesh mesh in model.Meshes)
        {
            foreach (Vertex vertex in mesh.Vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
        }

        Vector3 center = (min + max) * 0.5f;
        float reach = 0f;
        foreach (Mesh mesh in model.Meshes)
        {
            foreach (Vertex vertex in mesh.Vertices)
                reach = MathF.Max(reach, new Vector2(vertex.Position.X - center.X, vertex.Position.Z - center.Z).Length());
        }

        return (center, MathF.Max(1e-4f, MathF.Max(reach, (max.Y - min.Y) * 0.5f)));
    }

    private static void BakeFrame(Model model, int frame, Vector3 center, float half, byte[][] surfaces, byte[][] normals)
    {
        float angle = frame * MathF.PI * 2f / Frames;
        Vector3 toViewer = new(MathF.Sin(angle), 0f, MathF.Cos(angle));
        Vector3 right = new(-toViewer.Z, 0f, toViewer.X);
        int size = FrameSize * Samples;

        float[] depth = new float[size * size];
        Array.Fill(depth, float.MinValue);
        int[] owner = new int[size * size];
        Array.Fill(owner, -1);
        Vector2[] uvs = new Vector2[size * size];
        Vector3[] surfaceNormals = new Vector3[size * size];

        for (int m = 0; m < model.Meshes.Length; m++)
        {
            Mesh mesh = model.Meshes[m];
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                Vertex a = mesh.Vertices[mesh.Indices[i]], b = mesh.Vertices[mesh.Indices[i + 1]], c = mesh.Vertices[mesh.Indices[i + 2]];
                Vector3 face = Vector3.Normalize(Vector3.Cross(b.Position - a.Position, c.Position - a.Position));
                if (!float.IsFinite(face.X))
                    continue;

                RasterTriangle(Project(a), Project(b), Project(c), m, a, b, c, face, size, depth, owner, uvs, surfaceNormals);
            }
        }

        int originX = frame % Grid * FrameSize, originY = frame / Grid * FrameSize;
        for (int y = 0; y < FrameSize; y++)
        {
            for (int x = 0; x < FrameSize; x++)
            {
                for (int m = 0; m < model.Meshes.Length; m++)
                    Resolve(x, y, m, size, owner, uvs, surfaceNormals, surfaces[m], normals[m], ((originY + y) * AtlasSize) + originX + x);
            }
        }

        Vector3 Project(Vertex vertex)
        {
            Vector3 p = vertex.Position - center;
            float u = ((Vector3.Dot(p, right) / (2f * half)) + 0.5f) * size;
            float v = (0.5f - (p.Y / (2f * half))) * size;
            return new Vector3(u, v, Vector3.Dot(p, toViewer));
        }
    }

    /// <summary>
    /// One triangle into the frame's samples, nearest the viewer kept: its mesh, uv and normal (the vertices', else the
    /// face's) at each sample it covers.
    /// </summary>
    private static void RasterTriangle(Vector3 p0, Vector3 p1, Vector3 p2, int mesh, Vertex a, Vertex b, Vertex c, Vector3 face, int size, float[] depth, int[] owner, Vector2[] uvs, Vector3[] normals)
    {
        float area = Edge(p0, p1, p2.X, p2.Y);
        if (MathF.Abs(area) < 1e-12f)
            return;

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        bool hasNormals = a.Normal.LengthSquared() > 1e-8f && b.Normal.LengthSquared() > 1e-8f && c.Normal.LengthSquared() > 1e-8f;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float sx = x + 0.5f, sy = y + 0.5f;
                float w0 = Edge(p1, p2, sx, sy) / area, w1 = Edge(p2, p0, sx, sy) / area, w2 = Edge(p0, p1, sx, sy) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                int at = (y * size) + x;
                float z = (w0 * p0.Z) + (w1 * p1.Z) + (w2 * p2.Z);
                if (z <= depth[at])
                    continue;

                depth[at] = z;
                owner[at] = mesh;
                uvs[at] = (w0 * a.Uv) + (w1 * b.Uv) + (w2 * c.Uv);
                normals[at] = hasNormals ? Vector3.Normalize((w0 * a.Normal) + (w1 * b.Normal) + (w2 * c.Normal)) : face;
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y)
    {
        return ((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X));
    }

    /// <summary>
    /// One texel of one mesh's atlases from its samples: the share the mesh owns, and the average uv and normal of those.
    /// </summary>
    private static void Resolve(int x, int y, int mesh, int size, int[] owner, Vector2[] uvs, Vector3[] normals, byte[] surface, byte[] normal, int texel)
    {
        int covered = 0;
        Vector2 uv = Vector2.Zero;
        Vector3 n = Vector3.Zero;
        for (int sy = 0; sy < Samples; sy++)
        {
            for (int sx = 0; sx < Samples; sx++)
            {
                int at = (((y * Samples) + sy) * size) + (x * Samples) + sx;
                if (owner[at] != mesh)
                    continue;

                covered++;
                uv += uvs[at];
                n += normals[at];
            }
        }

        int o = texel * 4;
        surface[o + 3] = 255;
        normal[o + 3] = 255;
        if (covered == 0)
            return;

        uv /= covered;
        surface[o] = Unorm(uv.X - MathF.Floor(uv.X));
        surface[o + 1] = Unorm(uv.Y - MathF.Floor(uv.Y));
        surface[o + 2] = Unorm((float)covered / (Samples * Samples));
        // Plain xyz rather than octahedron encoded: filtering across the octahedron's folds makes garbage normals.
        Vector3 unit = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
        normal[o] = Unorm((unit.X * 0.5f) + 0.5f);
        normal[o + 1] = Unorm((unit.Y * 0.5f) + 0.5f);
        normal[o + 2] = Unorm((unit.Z * 0.5f) + 0.5f);
    }

    /// <summary>
    /// Gives empty texels the uv and normal of a covered neighbour in the same frame, a ring at a time.
    /// </summary>
    private static void Dilate(byte[] surface, byte[] normal)
    {
        for (int pass = 0; pass < Dilations; pass++)
        {
            byte[] filled = (byte[])surface.Clone(), filledNormal = (byte[])normal.Clone();
            bool[] has = new bool[AtlasSize * AtlasSize];
            for (int i = 0; i < has.Length; i++)
                has[i] = surface[(i * 4) + 2] > 0 || normal[i * 4] > 0 || normal[(i * 4) + 1] > 0 || normal[(i * 4) + 2] > 0; // a normal is never 0, 0, 0

            for (int y = 0; y < AtlasSize; y++)
            {
                for (int x = 0; x < AtlasSize; x++)
                {
                    int i = (y * AtlasSize) + x;
                    if (has[i])
                        continue;

                    foreach ((int dx, int dy) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= AtlasSize || ny >= AtlasSize || nx / FrameSize != x / FrameSize || ny / FrameSize != y / FrameSize)
                            continue;

                        int j = (ny * AtlasSize) + nx;
                        if (!has[j])
                            continue;

                        filled[i * 4] = surface[j * 4];
                        filled[(i * 4) + 1] = surface[(j * 4) + 1];
                        filledNormal[i * 4] = normal[j * 4];
                        filledNormal[(i * 4) + 1] = normal[(j * 4) + 1];
                        filledNormal[(i * 4) + 2] = normal[(j * 4) + 2];
                        break;
                    }
                }
            }

            Array.Copy(filled, surface, surface.Length);
            Array.Copy(filledNormal, normal, normal.Length);
        }
    }

    private static byte Unorm(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    /// <summary>
    /// The card of the model's mesh: a quad at the square's corners facing +Z, each vertex saying what it is.
    /// </summary>
    private static Mesh Card(Vector3 center, float half, ulong model, int mesh)
    {
        Mesh card = new() { Vertices = [.. ImpostorCard.Corners.Select(corner => ImpostorCard.Corner(corner, center, half, 0f, model, mesh))], Indices = [.. ImpostorCard.Indices] };
        card.ComputeBounds();
        return card;
    }
}
