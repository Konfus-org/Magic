using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.ClusterGenerator;

/// <summary>
/// Draws a mesh's card views (<see cref="Card"/>) on the CPU: each of <see cref="Card.Views"/> turns about +Y, looking
/// back at the mesh from that side, straight on (no perspective), into a cell of a <see cref="Card.Columns"/>-wide grid in
/// two atlases <see cref="Card.Size"/> texels square. Each texel is drawn from four samples, for soft edges, and the colours
/// of the texels the mesh covers are spread a few texels past its edge, so filtering at the edge reads the mesh rather
/// than nothing.
/// </summary>
internal static class CardBaker
{
    /// <summary>
    /// Texels a side of each view's cell.
    /// </summary>
    private const int CellSize = Card.Size / Card.Columns;

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
    internal static (Vector3 Centre, float Half, byte[] Surface, byte[] Normals) Bake(Mesh mesh)
    {
        ReadOnlySpan<Vertex> vertices = mesh.Vertices.Span;
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        Vector4 sphere = ClusterBuilder.ComputeSphere(vertices);
        Raster raster = new(new Vector3(sphere.X, sphere.Y, sphere.Z), MathF.Max(sphere.W, 1e-4f));
        byte[] surface = new byte[Card.Size * Card.Size * 4];
        byte[] normals = new byte[Card.Size * Card.Size * 4];
        for (int view = 0; view < Card.Views; view++)
        {
            // Seen from this side, looking back at the centre: right is +X when it looks along +Z, and up is +Y.
            float turn = view * MathF.Tau / Card.Views;
            Vector3 side = new(MathF.Sin(turn), 0, MathF.Cos(turn));
            Vector3 forward = -side;
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
            raster.Clear();
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                raster.Draw(vertices[(int)indices[t]], vertices[(int)indices[t + 1]], vertices[(int)indices[t + 2]], right, forward);
            }

            raster.Resolve(view % Card.Columns * CellSize, view / Card.Columns * CellSize, surface, normals);
        }

        Dilate(surface);
        Dilate(normals);
        return (raster.Centre, raster.Half, surface, normals);
    }

    /// <summary>
    /// Twice the signed area of the triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> on screen.
    /// </summary>
    private static float EdgeFunction(Vector3 a, Vector3 b, Vector3 c) => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);

    /// <summary>
    /// Spreads the colours of the covered texels <see cref="Spread"/> texels past their edge, within each view's cell,
    /// each uncovered texel taking the mean of its covered neighbours; the cover stays where it was.
    /// </summary>
    private static void Dilate(byte[] atlas)
    {
        bool[] filled = new bool[Card.Size * Card.Size];
        for (int i = 0; i < filled.Length; i++)
        {
            filled[i] = atlas[(i * 4) + 3] > 0;
        }

        for (int pass = 0; pass < Spread; pass++)
        {
            bool[] next = (bool[])filled.Clone();
            for (int y = 0; y < Card.Size; y++)
            {
                for (int x = 0; x < Card.Size; x++)
                {
                    int at = (y * Card.Size) + x;
                    if (filled[at] || !TryAverageNeighbours(atlas, filled, x, y, out Vector3 mean)) continue;

                    atlas[at * 4] = (byte)mean.X;
                    atlas[(at * 4) + 1] = (byte)mean.Y;
                    atlas[(at * 4) + 2] = (byte)mean.Z;
                    next[at] = true;
                }
            }

            filled = next;
        }
    }

    /// <summary>
    /// The mean colour of the covered texels among the eight around (<paramref name="x"/>, <paramref name="y"/>) within
    /// its view's cell; false when none of them is covered.
    /// </summary>
    private static bool TryAverageNeighbours(byte[] atlas, bool[] filled, int x, int y, out Vector3 mean)
    {
        int count = 0;
        Vector3 sum = Vector3.Zero;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx;
                int ny = y + dy;
                bool inCell = nx >= 0 && ny >= 0 && nx < Card.Size && ny < Card.Size && nx / CellSize == x / CellSize && ny / CellSize == y / CellSize;
                if (!inCell || !filled[(ny * Card.Size) + nx]) continue;

                int texel = ((ny * Card.Size) + nx) * 4;
                sum += new Vector3(atlas[texel], atlas[texel + 1], atlas[texel + 2]);
                count++;
            }
        }

        mean = count == 0 ? Vector3.Zero : sum / count;
        return count > 0;
    }

    /// <summary>
    /// One view's samples: the nearest depth, uv and normal the mesh put at each, <see cref="Samples"/> a side per texel
    /// of the view's cell, looking straight at the mesh from a side with its centre in the middle and its sphere filling
    /// the cell.
    /// </summary>
    private sealed class Raster(Vector3 centre, float half)
    {
        private const int Size = CellSize * Samples;

        private readonly float[] _depths = new float[Size * Size];

        private readonly Vector2[] _uvs = new Vector2[Size * Size];

        private readonly Vector3[] _faces = new Vector3[Size * Size];

        internal Vector3 Centre { get; } = centre;

        internal float Half { get; } = half;

        internal void Clear() => Array.Fill(_depths, float.PositiveInfinity);

        /// <summary>
        /// The triangle, seen from the side <paramref name="right"/> and <paramref name="forward"/> give, into the samples
        /// it covers where it is the nearest.
        /// </summary>
        internal void Draw(in Vertex a, in Vertex b, in Vertex c, Vector3 right, Vector3 forward)
        {
            Vector3 pa = Project(a.Position, right, forward);
            Vector3 pb = Project(b.Position, right, forward);
            Vector3 pc = Project(c.Position, right, forward);
            float area = EdgeFunction(pa, pb, pc);
            if (MathF.Abs(area) < 1e-12f) return;

            int fromX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
            int toX = Math.Min(Size - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
            int fromY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
            int toY = Math.Min(Size - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = fromX; x <= toX; x++)
                {
                    Vector3 point = new(x + 0.5f, y + 0.5f, 0);
                    float wa = EdgeFunction(pb, pc, point) / area;
                    float wb = EdgeFunction(pc, pa, point) / area;
                    float wc = 1 - wa - wb;
                    if (wa < 0 || wb < 0 || wc < 0) continue;

                    float depth = (wa * pa.Z) + (wb * pb.Z) + (wc * pc.Z);
                    int at = (y * Size) + x;
                    if (depth >= _depths[at]) continue;

                    _depths[at] = depth;
                    _uvs[at] = (wa * a.Uv) + (wb * b.Uv) + (wc * c.Uv);
                    _faces[at] = (wa * a.Normal) + (wb * b.Normal) + (wc * c.Normal);
                }
            }
        }

        /// <summary>
        /// The view's cell at (<paramref name="cellX"/>, <paramref name="cellY"/>) of both atlases from the samples: a
        /// texel's cover is the share of its samples the mesh is in, and its uv, normal and depth their mean over those,
        /// the depth in half sides behind the middle, -1 to 1.
        /// </summary>
        internal void Resolve(int cellX, int cellY, byte[] surface, byte[] normals)
        {
            for (int y = 0; y < CellSize; y++)
            {
                for (int x = 0; x < CellSize; x++)
                {
                    WriteTexel(x, y, (((cellY + y) * Card.Size) + cellX + x) * 4, surface, normals);
                }
            }
        }

        private Vector3 Project(Vector3 position, Vector3 right, Vector3 forward)
        {
            Vector3 local = position - Centre;
            float x = Vector3.Dot(local, right) / Half;
            float y = local.Y / Half;
            return new Vector3(((x * 0.5f) + 0.5f) * Size, (0.5f - (y * 0.5f)) * Size, Vector3.Dot(local, forward));
        }

        private void WriteTexel(int x, int y, int texel, byte[] surface, byte[] normals)
        {
            int covered = 0;
            Vector2 uv = Vector2.Zero;
            Vector3 normal = Vector3.Zero;
            float depth = 0;
            for (int sy = 0; sy < Samples; sy++)
            {
                for (int sx = 0; sx < Samples; sx++)
                {
                    int at = (((y * Samples) + sy) * Size) + (x * Samples) + sx;
                    if (float.IsPositiveInfinity(_depths[at])) continue;

                    covered++;
                    uv += _uvs[at];
                    normal += _faces[at];
                    depth += _depths[at];
                }
            }

            if (covered == 0) return;

            uv /= covered;
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            byte cover = (byte)(255 * covered / (Samples * Samples));
            surface[texel] = ToByte(uv.X - MathF.Floor(uv.X));
            surface[texel + 1] = ToByte(uv.Y - MathF.Floor(uv.Y));
            surface[texel + 2] = ToByte((depth / covered / Half * 0.5f) + 0.5f);
            surface[texel + 3] = cover;
            normals[texel] = ToByte((normal.X * 0.5f) + 0.5f);
            normals[texel + 1] = ToByte((normal.Y * 0.5f) + 0.5f);
            normals[texel + 2] = ToByte((normal.Z * 0.5f) + 0.5f);
            normals[texel + 3] = cover;
        }
    }
}
