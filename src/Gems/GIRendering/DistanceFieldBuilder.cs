using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;

namespace Magic.GIRendering;

/// <summary>
/// Builds a model's signed distance field: a cube of voxels around its bounds, each the distance to the nearest
/// triangle, negative inside, clamped to a band of a few voxels. The nearest triangle comes from a grid the
/// triangles are binned into, searched outward from the voxel's cell; the sign from the angle-weighted pseudo-normal
/// at the nearest point (Baerentzen and Aanaes, "Signed Distance Computation Using the Angle Weighted Pseudonormal",
/// 2005), which gives open meshes an inside behind their faces, or, past the band, from how many faces a ray out of
/// the voxel crosses. Pure functions over arrays, which a test drives with a known mesh.
/// </summary>
internal static class DistanceFieldBuilder
{
    /// <summary>
    /// How many voxels of distance the field keeps either side of a surface; beyond, it is as far.
    /// </summary>
    public const int Band = 4;

    /// <summary>
    /// The pseudo-normals a triangle has, one per <see cref="Feature"/>.
    /// </summary>
    private const int FeatureCount = 7;

    public static DistanceField Build(Model model)
    {
        List<Vector3> positions = [];
        List<(int A, int B, int C)> triangles = [];
        foreach (Mesh mesh in model.Meshes)
        {
            int first = positions.Count;
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                positions.Add(vertex.Position);
            }

            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                triangles.Add((first + (int)indices[i], first + (int)indices[i + 1], first + (int)indices[i + 2]));
            }
        }

        return Build([.. positions], [.. triangles]);
    }

    /// <summary>
    /// The field of a mesh given as positions and the triangles over them.
    /// </summary>
    public static DistanceField Build(Vector3[] positions, (int A, int B, int C)[] triangles)
    {
        if (positions.Length == 0 || triangles.Length == 0)
            return new DistanceField(
                [.. new byte[MeshDerived.FieldSize * MeshDerived.FieldSize * MeshDerived.FieldSize]],
                Vector3.Zero,
                1,
                Band
            );

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vector3 position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        // The field reaches a band beyond the bounds on each side, so a ray reads a true distance all the way in.
        Vector3 extent = max - min;
        float longest = Math.Max(Math.Max(extent.X, extent.Y), Math.Max(extent.Z, 1e-3f));
        float voxel = longest / (MeshDerived.FieldSize - 1 - (2 * Band));
        Vector3 center = (min + max) * 0.5f;
        Vector3 origin = center - new Vector3(voxel * (MeshDerived.FieldSize - 1) * 0.5f);
        float band = Band * voxel;

        Vector3[] pseudoNormals = GetPseudoNormals(positions, triangles);
        TriangleGrid grid = TriangleGrid.Create(positions, triangles, origin, voxel);
        byte[] field = new byte[MeshDerived.FieldSize * MeshDerived.FieldSize * MeshDerived.FieldSize];
        Parallel.For(
            0,
            MeshDerived.FieldSize,
            z =>
            {
                for (int y = 0; y < MeshDerived.FieldSize; y++)
                {
                    for (int x = 0; x < MeshDerived.FieldSize; x++)
                    {
                        Vector3 point = origin + (new Vector3(x, y, z) * voxel);
                        float distance = Measure(point, positions, triangles, pseudoNormals, grid, voxel, band);
                        float scaled = Math.Clamp(distance / band, -1, 1);
                        field[(((z * MeshDerived.FieldSize) + y) * MeshDerived.FieldSize) + x] = unchecked(
                            (byte)(sbyte)Math.Round(scaled * 127)
                        );
                    }
                }
            }
        );

        return new DistanceField([.. field], origin, voxel, band);
    }

    /// <summary>
    /// The signed distance from a point to the mesh, searched outward through the grid until no nearer triangle can
    /// be left; the band when none is within it.
    /// </summary>
    private static float Measure(
        Vector3 point,
        Vector3[] positions,
        (int A, int B, int C)[] triangles,
        Vector3[] pseudoNormals,
        TriangleGrid grid,
        float voxel,
        float band
    )
    {
        (int cellX, int cellY, int cellZ) = grid.GetCell(point);
        float best = float.MaxValue;
        Vector3 bestNormal = Vector3.UnitY;
        Vector3 bestPoint = point;
        for (int ring = 0; ring <= Band + 1; ring++)
        {
            // Everything in an inner ring is nearer than the ring's distance, so a best within it is the best.
            if (best <= (ring - 1) * voxel)
                break;

            for (int z = cellZ - ring; z <= cellZ + ring; z++)
            {
                for (int y = cellY - ring; y <= cellY + ring; y++)
                {
                    for (int x = cellX - ring; x <= cellX + ring; x++)
                    {
                        bool isOnRing =
                            Math.Abs(x - cellX) == ring || Math.Abs(y - cellY) == ring || Math.Abs(z - cellZ) == ring;
                        if (!isOnRing || !grid.TryGetCell(x, y, z, out ReadOnlySpan<int> cell))
                            continue;

                        foreach (int triangle in cell)
                        {
                            (int a, int b, int c) = triangles[triangle];
                            Vector3 closest = ClosestPoint(
                                point,
                                positions[a],
                                positions[b],
                                positions[c],
                                out Feature feature
                            );
                            float distance = Vector3.Distance(point, closest);
                            if (distance >= best)
                                continue;

                            best = distance;
                            bestPoint = closest;
                            bestNormal = pseudoNormals[(triangle * FeatureCount) + (int)feature];
                        }
                    }
                }
            }
        }

        if (best == float.MaxValue)
            return IsInside(point, positions, triangles, grid, voxel) ? -band : band;

        float sign = Vector3.Dot(point - bestPoint, bestNormal) < 0 ? -1 : 1;

        return sign * Math.Min(best, band);
    }

    /// <summary>
    /// Whether a point past the band is inside: a ray out of it along x crosses the surface an odd number of times.
    /// The ray starts a little off the voxel's centre, so it meets no edge or vertex exactly.
    /// </summary>
    private static bool IsInside(
        Vector3 point,
        Vector3[] positions,
        (int A, int B, int C)[] triangles,
        TriangleGrid grid,
        float voxel
    )
    {
        Vector3 origin = point + (new Vector3(0, 0.137f, 0.291f) * voxel);
        (int cellX, int cellY, int cellZ) = grid.GetCell(origin);
        HashSet<int> tested = [];
        int crossings = 0;
        for (int x = Math.Max(cellX, 0); x < MeshDerived.FieldSize; x++)
        {
            if (!grid.TryGetCell(x, cellY, cellZ, out ReadOnlySpan<int> cell))
                break;

            foreach (int triangle in cell)
            {
                if (!tested.Add(triangle))
                    continue;

                (int a, int b, int c) = triangles[triangle];
                if (CrossesAlongX(origin, positions[a], positions[b], positions[c]))
                    crossings++;
            }
        }

        return (crossings & 1) == 1;
    }

    /// <summary>
    /// Whether a ray from <paramref name="origin"/> along +x passes through the triangle (Möller and Trumbore, "Fast,
    /// Minimum Storage Ray-Triangle Intersection", 1997).
    /// </summary>
    private static bool CrossesAlongX(Vector3 origin, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 direction = Vector3.UnitX;
        Vector3 p = Vector3.Cross(direction, ac);
        float determinant = Vector3.Dot(ab, p);
        if (Math.Abs(determinant) < 1e-12f)
            return false;

        float inverse = 1 / determinant;
        Vector3 t = origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u is < 0 or > 1)
            return false;

        Vector3 q = Vector3.Cross(t, ab);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
            return false;

        return Vector3.Dot(ac, q) * inverse > 0;
    }

    /// <summary>
    /// The nearest point of a triangle to a point, and which part of the triangle it is on (Ericson, "Real-Time
    /// Collision Detection", 2005).
    /// </summary>
    private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Feature feature)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
        {
            feature = Feature.VertexA;
            return a;
        }

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
        {
            feature = Feature.VertexB;
            return b;
        }

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            feature = Feature.EdgeAb;
            return a + (ab * (d1 / (d1 - d3)));
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
        {
            feature = Feature.VertexC;
            return c;
        }

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            feature = Feature.EdgeCa;
            return a + (ac * (d2 / (d2 - d6)));
        }

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            feature = Feature.EdgeBc;
            return b + ((c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6))));
        }

        float denominator = 1 / (va + vb + vc);
        feature = Feature.Face;

        return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }

    /// <summary>
    /// By triangle, a pseudo-normal per <see cref="Feature"/> in the enum's order: the face's own, a vertex's faces'
    /// weighted by their angles there, an edge's two faces' sum; the face's own where a sum is zero.
    /// </summary>
    private static Vector3[] GetPseudoNormals(Vector3[] positions, (int A, int B, int C)[] triangles)
    {
        Vector3[] faces = new Vector3[triangles.Length];
        Vector3[] vertices = new Vector3[positions.Length];
        Dictionary<(int, int), Vector3> edges = [];
        for (int t = 0; t < triangles.Length; t++)
        {
            (int a, int b, int c) = triangles[t];
            Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            float length = normal.Length();
            if (length < 1e-12f)
                continue;

            normal /= length;
            faces[t] = normal;
            vertices[a] += normal * Angle(positions[a], positions[b], positions[c]);
            vertices[b] += normal * Angle(positions[b], positions[c], positions[a]);
            vertices[c] += normal * Angle(positions[c], positions[a], positions[b]);
            AddEdge(a, b, normal);
            AddEdge(b, c, normal);
            AddEdge(c, a, normal);
        }

        Vector3[] pseudoNormals = new Vector3[triangles.Length * FeatureCount];
        for (int t = 0; t < triangles.Length; t++)
        {
            (int a, int b, int c) = triangles[t];
            Span<Vector3> own = pseudoNormals.AsSpan(t * FeatureCount, FeatureCount);
            own[(int)Feature.Face] = faces[t];
            own[(int)Feature.VertexA] = vertices[a];
            own[(int)Feature.VertexB] = vertices[b];
            own[(int)Feature.VertexC] = vertices[c];
            own[(int)Feature.EdgeAb] = edges.GetValueOrDefault(Key(a, b));
            own[(int)Feature.EdgeBc] = edges.GetValueOrDefault(Key(b, c));
            own[(int)Feature.EdgeCa] = edges.GetValueOrDefault(Key(c, a));
            for (int i = 0; i < own.Length; i++)
            {
                if (own[i].LengthSquared() == 0)
                    own[i] = faces[t];
            }
        }

        return pseudoNormals;

        static float Angle(Vector3 at, Vector3 next, Vector3 previous)
        {
            Vector3 u = next - at;
            Vector3 v = previous - at;
            float lengths = u.Length() * v.Length();

            return lengths > 0 ? MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / lengths, -1, 1)) : 0;
        }

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        void AddEdge(int a, int b, Vector3 normal)
        {
            (int, int) key = Key(a, b);
            edges[key] = edges.TryGetValue(key, out Vector3 sum) ? sum + normal : normal;
        }
    }

    private enum Feature : byte
    {
        Face,
        VertexA,
        VertexB,
        VertexC,
        EdgeAb,
        EdgeBc,
        EdgeCa,
    }

    /// <summary>
    /// The triangles binned into the field's cells by their bounds, so a voxel looks at the few near it.
    /// </summary>
    private sealed class TriangleGrid(Vector3 origin, float voxel, int[] starts, int[] items)
    {
        public static TriangleGrid Create(
            Vector3[] positions,
            (int A, int B, int C)[] triangles,
            Vector3 origin,
            float voxel
        )
        {
            int[] counts = new int[(MeshDerived.FieldSize * MeshDerived.FieldSize * MeshDerived.FieldSize) + 1];
            (int Min, int Max)[] rangesX = new (int, int)[triangles.Length];
            (int Min, int Max)[] rangesY = new (int, int)[triangles.Length];
            (int Min, int Max)[] rangesZ = new (int, int)[triangles.Length];
            for (int t = 0; t < triangles.Length; t++)
            {
                (int a, int b, int c) = triangles[t];
                Vector3 min = Vector3.Min(Vector3.Min(positions[a], positions[b]), positions[c]);
                Vector3 max = Vector3.Max(Vector3.Max(positions[a], positions[b]), positions[c]);
                rangesX[t] = Range(min.X, max.X, origin.X, voxel);
                rangesY[t] = Range(min.Y, max.Y, origin.Y, voxel);
                rangesZ[t] = Range(min.Z, max.Z, origin.Z, voxel);
                for (int z = rangesZ[t].Min; z <= rangesZ[t].Max; z++)
                {
                    for (int y = rangesY[t].Min; y <= rangesY[t].Max; y++)
                    {
                        for (int x = rangesX[t].Min; x <= rangesX[t].Max; x++)
                        {
                            counts[Index(x, y, z) + 1]++;
                        }
                    }
                }
            }

            for (int i = 1; i < counts.Length; i++)
            {
                counts[i] += counts[i - 1];
            }

            int[] starts = (int[])counts.Clone();
            int[] items = new int[counts[^1]];
            int[] fill = (int[])counts.Clone();
            for (int t = 0; t < triangles.Length; t++)
            {
                for (int z = rangesZ[t].Min; z <= rangesZ[t].Max; z++)
                {
                    for (int y = rangesY[t].Min; y <= rangesY[t].Max; y++)
                    {
                        for (int x = rangesX[t].Min; x <= rangesX[t].Max; x++)
                        {
                            items[fill[Index(x, y, z)]++] = t;
                        }
                    }
                }
            }

            return new TriangleGrid(origin, voxel, starts, items);
        }

        /// <summary>
        /// The cell a point falls in, which may lie outside the grid.
        /// </summary>
        public (int X, int Y, int Z) GetCell(Vector3 point)
        {
            Vector3 cell = (point - origin) / voxel;

            return ((int)Math.Floor(cell.X), (int)Math.Floor(cell.Y), (int)Math.Floor(cell.Z));
        }

        public bool TryGetCell(int x, int y, int z, out ReadOnlySpan<int> cell)
        {
            cell = default;
            if (
                x < 0
                || y < 0
                || z < 0
                || x >= MeshDerived.FieldSize
                || y >= MeshDerived.FieldSize
                || z >= MeshDerived.FieldSize
            )
                return false;

            int index = Index(x, y, z);
            cell = items.AsSpan(starts[index], starts[index + 1] - starts[index]);

            return true;
        }

        private static int Index(int x, int y, int z) =>
            (((z * MeshDerived.FieldSize) + y) * MeshDerived.FieldSize) + x;

        private static (int Min, int Max) Range(float min, float max, float origin, float voxel) =>
            (
                Math.Clamp((int)Math.Floor((min - origin) / voxel), 0, MeshDerived.FieldSize - 1),
                Math.Clamp((int)Math.Floor((max - origin) / voxel), 0, MeshDerived.FieldSize - 1)
            );
    }
}

/// <summary>
/// A model's distance field: <see cref="MeshDerived.FieldSize"/> voxels a side from <paramref name="Origin"/>,
/// <paramref name="Voxel"/> metres apart, each the distance scaled by <paramref name="Band"/> as a signed byte.
/// </summary>
internal readonly record struct DistanceField(ImmutableArray<byte> Voxels, Vector3 Origin, float Voxel, float Band);
