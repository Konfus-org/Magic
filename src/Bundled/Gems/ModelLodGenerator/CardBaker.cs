using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.ModelLodGenerator;

/// <summary>
/// Draws a mesh's card views (<see cref="Card"/>) on the CPU: each of <see cref="Card.Views"/> turns about +Y, looking
/// back at the mesh from that side, straight on (no perspective), into a cell of a <see cref="Card.Columns"/>-wide grid in
/// two atlases <see cref="Card.Size"/> texels square. Each texel is drawn from four samples, for soft edges, and the colours
/// of the texels the mesh covers are spread a few texels past its edge, so filtering at the edge reads the mesh rather
/// than nothing.
/// </summary>
internal static class CardBaker
{
    private const int Views = Card.Views;
    private const int Columns = Card.Columns;
    private const int AtlasSize = Card.Size;

    /// <summary>
    /// Texels a side of each view's cell.
    /// </summary>
    private const int CellSize = AtlasSize / Columns;

    /// <summary>
    /// Samples a side of each texel.
    /// </summary>
    private const int Samples = 2;

    /// <summary>
    /// Texels the covered colours are spread past the edge.
    /// </summary>
    private const int Spread = 4;

    /// <summary>
    /// <paramref name="mesh"/>'s card: where it stands, half its side, and its two atlases as RGBA8 bytes, the surface
    /// (material uv in red and green, how far behind the middle in blue, cover in alpha) and the normals (xyz * 0.5 + 0.5,
    /// cover in alpha).
    /// </summary>
    public static (Vector3 Centre, float Half, byte[] Surface, byte[] Normals) Bake(Mesh mesh)
    {
        ReadOnlySpan<Vertex> vertices = mesh.Vertices.Span;
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        Vector3 centre = (min + max) / 2;
        float half = 0;
        foreach (Vertex vertex in vertices)
        {
            half = MathF.Max(half, Vector3.Distance(vertex.Position, centre));
        }

        half = MathF.Max(half, 1e-4f);
        byte[] surface = new byte[AtlasSize * AtlasSize * 4];
        byte[] normals = new byte[AtlasSize * AtlasSize * 4];
        int size = CellSize * Samples;
        float[] depths = new float[size * size];
        Vector2[] uvs = new Vector2[size * size];
        Vector3[] faces = new Vector3[size * size];
        for (int view = 0; view < Views; view++)
        {
            // Seen from this side, looking back at the centre: right is +X when it looks along +Z, and up is +Y.
            float turn = view * MathF.Tau / Views;
            Vector3 side = new(MathF.Sin(turn), 0, MathF.Cos(turn));
            Vector3 forward = -side;
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
            Array.Fill(depths, float.PositiveInfinity);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                Draw(vertices[(int)indices[t]], vertices[(int)indices[t + 1]], vertices[(int)indices[t + 2]]);
            }

            Resolve(view % Columns * CellSize, view / Columns * CellSize);

            void Draw(in Vertex a, in Vertex b, in Vertex c)
            {
                Vector3 pa = Project(a.Position);
                Vector3 pb = Project(b.Position);
                Vector3 pc = Project(c.Position);
                float area = Edge(pa, pb, pc);
                if (MathF.Abs(area) < 1e-12f)
                {
                    return;
                }

                int fromX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
                int toX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
                int fromY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
                int toY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
                for (int y = fromY; y <= toY; y++)
                {
                    for (int x = fromX; x <= toX; x++)
                    {
                        Vector3 point = new(x + 0.5f, y + 0.5f, 0);
                        float wa = Edge(pb, pc, point) / area;
                        float wb = Edge(pc, pa, point) / area;
                        float wc = 1 - wa - wb;
                        if (wa < 0 || wb < 0 || wc < 0)
                        {
                            continue;
                        }

                        float depth = (wa * pa.Z) + (wb * pb.Z) + (wc * pc.Z);
                        int at = (y * size) + x;
                        if (depth >= depths[at])
                        {
                            continue;
                        }

                        depths[at] = depth;
                        uvs[at] = (wa * a.Uv) + (wb * b.Uv) + (wc * c.Uv);
                        faces[at] = (wa * a.Normal) + (wb * b.Normal) + (wc * c.Normal);
                    }
                }
            }

            Vector3 Project(Vector3 position)
            {
                Vector3 local = position - centre;
                float x = Vector3.Dot(local, right) / half;
                float y = local.Y / half;
                return new Vector3(((x * 0.5f) + 0.5f) * size, (0.5f - (y * 0.5f)) * size, Vector3.Dot(local, forward));
            }
        }

        Dilate(surface);
        Dilate(normals);
        return (centre, half, surface, normals);

        // Each texel of the view's cell from its samples: the cover is the share of them the mesh is in, the uv, the
        // normal and the depth their mean over those, the depth in half sides behind the middle, -1 to 1.
        void Resolve(int cellX, int cellY)
        {
            for (int y = 0; y < CellSize; y++)
            {
                for (int x = 0; x < CellSize; x++)
                {
                    int covered = 0;
                    Vector2 uv = Vector2.Zero;
                    Vector3 normal = Vector3.Zero;
                    float depth = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            int at = (((y * Samples) + sy) * size) + (x * Samples) + sx;
                            if (float.IsPositiveInfinity(depths[at]))
                            {
                                continue;
                            }

                            covered++;
                            uv += uvs[at];
                            normal += faces[at];
                            depth += depths[at];
                        }
                    }

                    if (covered == 0)
                    {
                        continue;
                    }

                    uv /= covered;
                    normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
                    byte cover = (byte)(255 * covered / (Samples * Samples));
                    int texel = (((cellY + y) * AtlasSize) + cellX + x) * 4;
                    surface[texel] = Unit(uv.X - MathF.Floor(uv.X));
                    surface[texel + 1] = Unit(uv.Y - MathF.Floor(uv.Y));
                    surface[texel + 2] = Unit((depth / covered / half * 0.5f) + 0.5f);
                    surface[texel + 3] = cover;
                    normals[texel] = Unit((normal.X * 0.5f) + 0.5f);
                    normals[texel + 1] = Unit((normal.Y * 0.5f) + 0.5f);
                    normals[texel + 2] = Unit((normal.Z * 0.5f) + 0.5f);
                    normals[texel + 3] = cover;
                }
            }
        }
    }

    /// <summary>
    /// Twice the signed area of the triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> on screen.
    /// </summary>
    private static float Edge(Vector3 a, Vector3 b, Vector3 c) => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static byte Unit(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);

    /// <summary>
    /// Spreads the colours of the covered texels <see cref="Spread"/> texels past their edge, within each view's cell,
    /// each uncovered texel taking the mean of its covered neighbours; the cover stays where it was.
    /// </summary>
    private static void Dilate(byte[] atlas)
    {
        bool[] filled = new bool[AtlasSize * AtlasSize];
        for (int i = 0; i < filled.Length; i++)
        {
            filled[i] = atlas[(i * 4) + 3] > 0;
        }

        for (int pass = 0; pass < Spread; pass++)
        {
            bool[] next = (bool[])filled.Clone();
            for (int y = 0; y < AtlasSize; y++)
            {
                for (int x = 0; x < AtlasSize; x++)
                {
                    int at = (y * AtlasSize) + x;
                    if (filled[at])
                    {
                        continue;
                    }

                    int count = 0;
                    Vector3 sum = Vector3.Zero;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= AtlasSize || ny >= AtlasSize || nx / CellSize != x / CellSize || ny / CellSize != y / CellSize || !filled[(ny * AtlasSize) + nx])
                            {
                                continue;
                            }

                            int texel = ((ny * AtlasSize) + nx) * 4;
                            sum += new Vector3(atlas[texel], atlas[texel + 1], atlas[texel + 2]);
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    sum /= count;
                    atlas[at * 4] = (byte)sum.X;
                    atlas[(at * 4) + 1] = (byte)sum.Y;
                    atlas[(at * 4) + 2] = (byte)sum.Z;
                    next[at] = true;
                }
            }

            filled = next;
        }
    }
}
