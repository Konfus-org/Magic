using System.Numerics;
using Magic.DeferredRendering.World;
using Magic.Graphics;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Bakes a model's octahedral impostor atlas on the CPU, as <see cref="ModelClusters.Impostor"/> says.
/// </summary>
/// <remarks>
/// It bakes from the clusters that the hierarchy draws at an atlas texel's error, so a model of any size bakes from
/// about as many triangles as its atlas can show. Each view rasterises them orthographically into its tile, and keeps
/// the nearest. The gem's <c>Scene/Impostors.slang</c> reads the atlas with the same grid and view frames.
/// </remarks>
internal static class ImpostorBuilder
{
    /// <summary>
    /// Views across and down an atlas, on an octahedral grid over the whole sphere.
    /// </summary>
    public const int Views = 12;

    /// <summary>
    /// Texels a side of one view of an atlas.
    /// </summary>
    public const int Tile = 64;

    public const int Size = Views * Tile;

    /// <summary>
    /// Bakes the atlas as RGBA16F halves, the texture coordinates of the same texels as RG16F halves, row by row, and
    /// each view's silhouette bounds as <see cref="ModelClusters.ImpostorViewBounds"/> says.
    /// </summary>
    public static (Half[] Surface, Half[] Uv, uint[] ViewBounds) Bake(
        IReadOnlyList<GpuVertex> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<GpuCluster> clusters,
        Vector3 center,
        float radius
    )
    {
        Half[] texels = new Half[Size * Size * 4];
        Half[] uvs = new Half[Size * Size * 2];
        uint[] viewBounds = new uint[Views * Views];

        // The cut the GPU's cull makes when the error it may show is a texel of a view: a cluster whose own error is
        // under it and whose parent group's is over it.
        float texelError = 2 * radius / Tile;
        Parallel.For(0, Views * Views, view => BakeView(view % Views, view / Views));

        return (texels, uvs, viewBounds);

        void BakeView(int viewX, int viewY)
        {
            (Vector3 forward, Vector3 right, Vector3 up) = GetViewFrame(viewX, viewY);
            float[] depth = new float[Tile * Tile];
            Array.Fill(depth, float.MaxValue);
            foreach (GpuCluster cluster in clusters)
            {
                if (cluster.Error > texelError || cluster.ParentError <= texelError)
                    continue;

                for (int t = 0; t < cluster.TriangleCount; t++)
                {
                    int first = (int)cluster.IndexOffset + (t * 3);
                    DrawTriangle(
                        vertices[(int)indices[first]],
                        vertices[(int)indices[first + 1]],
                        vertices[(int)indices[first + 2]],
                        cluster.MaterialSlot
                    );
                }
            }

            viewBounds[(viewY * Views) + viewX] = GetBounds(depth);

            void DrawTriangle(GpuVertex a, GpuVertex b, GpuVertex c, uint slot)
            {
                Vector3 pa = Project(a.Position);
                Vector3 pb = Project(b.Position);
                Vector3 pc = Project(c.Position);
                float area = Edge(pa, pb, pc);
                if (MathF.Abs(area) < 1e-12f)
                    return;

                int minX = Math.Max((int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))), 0);
                int maxX = Math.Min((int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))), Tile - 1);
                int minY = Math.Max((int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))), 0);
                int maxY = Math.Min((int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))), Tile - 1);
                Vector3 na = GpuPacker.DecodeUnitVector(a.Normal);
                Vector3 nb = GpuPacker.DecodeUnitVector(b.Normal);
                Vector3 nc = GpuPacker.DecodeUnitVector(c.Normal);

                // A triangle facing away from the view is left out, as the cluster draw culls it, so the impostor
                // shows what the clusters it hands off from show.
                if (Vector3.Dot(na + nb + nc, forward) <= 0)
                    return;
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector3 p = new(x + 0.5f, y + 0.5f, 0);
                        float wa = Edge(pb, pc, p) / area;
                        float wb = Edge(pc, pa, p) / area;
                        float wc = Edge(pa, pb, p) / area;
                        if (wa < 0 || wb < 0 || wc < 0)
                            continue;

                        float z = (wa * pa.Z) + (wb * pb.Z) + (wc * pc.Z);
                        if (z >= depth[(y * Tile) + x])
                            continue;

                        depth[(y * Tile) + x] = z;
                        Vector3 normal = Vector3.Normalize((wa * na) + (wb * nb) + (wc * nc));
                        Vector2 flat = GpuPacker.EncodeOctahedral(normal);
                        int texel = ((((viewY * Tile) + y) * Size) + (viewX * Tile) + x) * 4;
                        texels[texel] = (Half)flat.X;
                        texels[texel + 1] = (Half)flat.Y;
                        texels[texel + 2] = (Half)Math.Clamp(z, 0, 1);
                        texels[texel + 3] = (Half)(slot + 1);
                        Vector2 uv = (wa * a.Uv) + (wb * b.Uv) + (wc * c.Uv);
                        uvs[texel / 2] = (Half)uv.X;
                        uvs[(texel / 2) + 1] = (Half)uv.Y;
                    }
                }
            }

            // A model point as the tile's pixel x, y and its depth from the view's front plane over the diameter.
            Vector3 Project(Vector3 point)
            {
                Vector3 offset = point - center;
                float s = Vector3.Dot(offset, right) / radius;
                float t = Vector3.Dot(offset, up) / radius;
                float along = Vector3.Dot(offset, forward);

                return new Vector3((s + 1) * 0.5f * Tile, (1 - t) * 0.5f * Tile, (radius - along) / (2 * radius));
            }
        }
    }

    /// <summary>
    /// Gets the texels that a view saw something at, as <see cref="ModelClusters.ImpostorViewBounds"/> packs them.
    /// </summary>
    private static uint GetBounds(float[] depth)
    {
        int minX = Tile;
        int minY = Tile;
        int endX = 0;
        int endY = 0;
        for (int y = 0; y < Tile; y++)
        {
            for (int x = 0; x < Tile; x++)
            {
                if (depth[(y * Tile) + x] == float.MaxValue)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                endX = Math.Max(endX, x + 1);
                endY = Math.Max(endY, y + 1);
            }
        }

        return endX == 0 ? 0 : (uint)(minX | (minY << 8) | (endX << 16) | (endY << 24));
    }

    /// <summary>
    /// Gets the view at grid point x, y: the direction it looks from, towards the model, and its right and up.
    /// </summary>
    private static (Vector3 Forward, Vector3 Right, Vector3 Up) GetViewFrame(int x, int y)
    {
        Vector2 flat = (new Vector2(x, y) / (Views - 1) * 2) - Vector2.One;
        Vector3 forward = GpuPacker.DecodeOctahedral(flat);
        Vector3 up0 = MathF.Abs(forward.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up0, forward));
        Vector3 up = Vector3.Cross(forward, right);

        return (forward, right, up);
    }

    private static float Edge(Vector3 a, Vector3 b, Vector3 p) =>
        ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
}
