using Magic.Contexts.Assets;
using System.Numerics;

namespace MeshLods;

/// <summary>
/// Sven Forstmann's fast quadric mesh simplification: Garland and Heckbert's quadric error metrics, but instead of a
/// priority queue of edge collapses each pass collapses every edge whose error is under a threshold that grows from pass
/// to pass, so it runs in near linear time. Reference: https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification
/// (MIT), and Garland and Heckbert, "Surface Simplification Using Quadric Error Metrics", SIGGRAPH 1997.
/// <para>
/// The collapses work on positions only: vertices at the same place are welded first, so a uv or normal seam (a
/// cylinder's cap, a cube's edges) is not a border that cannot move. What a vertex carries besides its place stays with
/// each triangle's corner, from the vertex the corner had, so the seams come out where they were with the corners moved.
/// </para>
/// </summary>
internal static class QuadricSimplifier
{
    /// <summary>
    /// How fast the threshold grows from pass to pass: higher is faster and rougher (the reference's default).
    /// </summary>
    private const double Aggressiveness = 7.0;

    private const int MaxPasses = 100;

    /// <summary>
    /// The mesh brought down to about <paramref name="share"/> of its triangles, or as near as it goes without moving the
    /// surface further than <paramref name="maxError"/> (in the mesh's units) or tearing its borders; tangents, uvs and
    /// normals come from the corners' own vertices. The reference only stops at the triangle count, which takes a box
    /// down to a spike: the error is what keeps a shape that has nothing to spare as it is.
    /// </summary>
    public static Mesh Simplify(Mesh mesh, float share, float maxError)
    {
        if (mesh.Indices.Length < 3)
            return mesh;

        Simplification work = new(mesh);
        work.Run(Math.Max(1, (int)(mesh.Indices.Length / 3 * share)), (double)maxError * maxError);
        return work.ToMesh();
    }

    private sealed class Simplification
    {
        private readonly Mesh _source;
        private readonly List<Triangle> _triangles = [];
        private readonly List<Point> _points = [];
        private readonly List<(int Triangle, int Corner)> _refs = [];

        public Simplification(Mesh source)
        {
            _source = source;

            // Weld by position: the welded points are what collapses, each corner keeps its source vertex for the rest.
            Dictionary<Vector3, int> pointOf = [];
            int[] weld = new int[source.Vertices.Length];
            for (int i = 0; i < source.Vertices.Length; i++)
            {
                Vector3 position = source.Vertices[i].Position;
                if (!pointOf.TryGetValue(position, out int point))
                {
                    point = _points.Count;
                    pointOf[position] = point;
                    _points.Add(new Point { Position = new Vector3d(position) });
                }

                weld[i] = point;
            }

            for (int i = 0; i + 2 < source.Indices.Length; i += 3)
            {
                uint a = source.Indices[i], b = source.Indices[i + 1], c = source.Indices[i + 2];
                Triangle triangle = new() { V = [weld[a], weld[b], weld[c]], Source = [(int)a, (int)b, (int)c] };
                if (triangle.V[0] != triangle.V[1] && triangle.V[1] != triangle.V[2] && triangle.V[0] != triangle.V[2])
                    _triangles.Add(triangle);
            }
        }

        public void Run(int target, double maxError)
        {
            int deleted = 0;
            int count = _triangles.Count;
            bool[] flipped0 = [], flipped1 = [];
            for (int pass = 0; pass < MaxPasses && count - deleted > target; pass++)
            {
                if (pass % 5 == 0)
                    Update(pass);

                foreach (Triangle triangle in _triangles)
                    triangle.Dirty = false;

                // The quadric error is a sum of squared distances, so it is held under the square of the distance allowed.
                double threshold = Math.Min(0.000000001 * Math.Pow(pass + 3, Aggressiveness), maxError);
                for (int t = 0; t < _triangles.Count && count - deleted > target; t++)
                {
                    Triangle triangle = _triangles[t];
                    if (triangle.Error[3] > threshold || triangle.Deleted || triangle.Dirty)
                        continue;

                    for (int edge = 0; edge < 3; edge++)
                    {
                        if (triangle.Error[edge] > threshold)
                            continue;

                        int i0 = triangle.V[edge], i1 = triangle.V[(edge + 1) % 3];
                        Point v0 = _points[i0], v1 = _points[i1];
                        if (v0.Border != v1.Border)
                            continue; // an edge along a border collapses along it; one across would pull the border in

                        CollapseError(i0, i1, out Vector3d place);
                        if (flipped0.Length < v0.Count)
                            Array.Resize(ref flipped0, v0.Count * 2);
                        if (flipped1.Length < v1.Count)
                            Array.Resize(ref flipped1, v1.Count * 2);
                        if (Flipped(place, i1, v0, flipped0) || Flipped(place, i0, v1, flipped1))
                            continue;

                        v0.Position = place;
                        v0.Quadric += v1.Quadric;
                        int start = _refs.Count;
                        deleted += Retarget(i0, v0, flipped0);
                        deleted += Retarget(i0, v1, flipped1);
                        int added = _refs.Count - start;
                        if (added <= v0.Count)
                        {
                            // Fits where v0's references were: move them there, so the list does not grow.
                            for (int r = 0; r < added; r++)
                                _refs[v0.Start + r] = _refs[start + r];
                            _refs.RemoveRange(start, added);
                        }
                        else
                        {
                            v0.Start = start;
                        }

                        v0.Count = added;
                        break;
                    }
                }
            }

            _triangles.RemoveAll(triangle => triangle.Deleted);
        }

        /// <summary>
        /// The mesh as it is now: one vertex per (point, source vertex) pair a corner uses, at the point's place with the
        /// source vertex's normal, tangent and uv.
        /// </summary>
        public Mesh ToMesh()
        {
            Dictionary<(int Point, int Source), uint> made = [];
            List<Vertex> vertices = [];
            List<uint> indices = [];
            foreach (Triangle triangle in _triangles)
            {
                if (triangle.Deleted)
                    continue;

                for (int corner = 0; corner < 3; corner++)
                {
                    (int point, int source) = (triangle.V[corner], triangle.Source[corner]);
                    if (!made.TryGetValue((point, source), out uint index))
                    {
                        Vertex vertex = _source.Vertices[source];
                        vertex.Position = _points[point].Position.ToVector3();
                        index = (uint)vertices.Count;
                        vertices.Add(vertex);
                        made[(point, source)] = index;
                    }

                    indices.Add(index);
                }
            }

            Mesh mesh = new() { Vertices = [.. vertices], Indices = [.. indices] };
            mesh.ComputeBounds();
            return mesh;
        }

        /// <summary>
        /// Whether moving the point at the other end of <paramref name="other"/>'s edge to <paramref name="place"/> would
        /// turn one of <paramref name="point"/>'s triangles over (or make it a sliver); marks those that would lose the
        /// edge, which the collapse deletes.
        /// </summary>
        private bool Flipped(Vector3d place, int other, Point point, bool[] deleted)
        {
            for (int r = 0; r < point.Count; r++)
            {
                (int t, int corner) = _refs[point.Start + r];
                Triangle triangle = _triangles[t];
                if (triangle.Deleted)
                    continue;

                int id1 = triangle.V[(corner + 1) % 3], id2 = triangle.V[(corner + 2) % 3];
                if (id1 == other || id2 == other)
                {
                    deleted[r] = true;
                    continue;
                }

                Vector3d d1 = (_points[id1].Position - place).Normalized();
                Vector3d d2 = (_points[id2].Position - place).Normalized();
                if (Math.Abs(Vector3d.Dot(d1, d2)) > 0.999)
                    return true;

                Vector3d normal = Vector3d.Cross(d1, d2).Normalized();
                deleted[r] = false;
                if (Vector3d.Dot(normal, triangle.Normal) < 0.2)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Points <paramref name="point"/>'s triangles at <paramref name="into"/>, deleting those the collapse removed;
        /// the references go on the end of the list. Answers how many were deleted.
        /// </summary>
        private int Retarget(int into, Point point, bool[] deleted)
        {
            int removed = 0;
            for (int r = 0; r < point.Count; r++)
            {
                (int t, int corner) = _refs[point.Start + r];
                Triangle triangle = _triangles[t];
                if (triangle.Deleted)
                    continue;

                if (deleted[r])
                {
                    triangle.Deleted = true;
                    removed++;
                    continue;
                }

                triangle.V[corner] = into;
                triangle.Dirty = true;
                SetErrors(triangle);
                _refs.Add((t, corner));
            }

            return removed;
        }

        /// <summary>
        /// Compacts the triangles and rebuilds the references; the first time, also the quadrics, the errors and the borders.
        /// </summary>
        private void Update(int pass)
        {
            if (pass > 0)
                _triangles.RemoveAll(triangle => triangle.Deleted);

            foreach (Point point in _points)
            {
                point.Start = 0;
                point.Count = 0;
            }

            foreach (Triangle triangle in _triangles)
            {
                _points[triangle.V[0]].Count++;
                _points[triangle.V[1]].Count++;
                _points[triangle.V[2]].Count++;
            }

            int start = 0;
            foreach (Point point in _points)
            {
                point.Start = start;
                start += point.Count;
                point.Count = 0;
            }

            _refs.Clear();
            _refs.AddRange(Enumerable.Repeat((0, 0), start));
            for (int t = 0; t < _triangles.Count; t++)
            {
                Triangle triangle = _triangles[t];
                for (int corner = 0; corner < 3; corner++)
                {
                    Point point = _points[triangle.V[corner]];
                    _refs[point.Start + point.Count] = (t, corner);
                    point.Count++;
                }
            }

            if (pass != 0)
                return;

            // A border is an edge with one triangle: its points only collapse into each other, or the mesh would shrink away from it.
            foreach (Point point in _points)
            {
                Dictionary<int, int> uses = [];
                for (int r = 0; r < point.Count; r++)
                {
                    Triangle triangle = _triangles[_refs[point.Start + r].Triangle];
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int id = triangle.V[corner];
                        uses[id] = uses.GetValueOrDefault(id) + 1;
                    }
                }

                foreach ((int id, int count) in uses)
                {
                    if (count == 1)
                        _points[id].Border = true;
                }
            }

            foreach (Triangle triangle in _triangles)
            {
                Vector3d p0 = _points[triangle.V[0]].Position;
                Vector3d normal = Vector3d.Cross(_points[triangle.V[1]].Position - p0, _points[triangle.V[2]].Position - p0).Normalized();
                triangle.Normal = normal;
                SymmetricMatrix plane = new(normal.X, normal.Y, normal.Z, -Vector3d.Dot(normal, p0));
                _points[triangle.V[0]].Quadric += plane;
                _points[triangle.V[1]].Quadric += plane;
                _points[triangle.V[2]].Quadric += plane;
            }

            foreach (Triangle triangle in _triangles)
                SetErrors(triangle);
        }

        /// <summary>
        /// The error of collapsing each of the triangle's edges, and the least of them.
        /// </summary>
        private void SetErrors(Triangle triangle)
        {
            for (int edge = 0; edge < 3; edge++)
                triangle.Error[edge] = CollapseError(triangle.V[edge], triangle.V[(edge + 1) % 3], out _);
            triangle.Error[3] = Math.Min(triangle.Error[0], Math.Min(triangle.Error[1], triangle.Error[2]));
        }

        /// <summary>
        /// The error of collapsing the edge, and the place the point goes: where the summed quadric is least, or the best of
        /// the two ends and their middle when that cannot be solved (or an end is on a border).
        /// </summary>
        private double CollapseError(int id1, int id2, out Vector3d place)
        {
            Point p1 = _points[id1], p2 = _points[id2];
            SymmetricMatrix q = p1.Quadric + p2.Quadric;
            bool border = p1.Border && p2.Border;
            double det = q.Det(0, 1, 2, 1, 4, 5, 2, 5, 7);
            if (det != 0 && !border)
            {
                place = new Vector3d(
                    -1 / det * q.Det(1, 2, 3, 4, 5, 6, 5, 7, 8),
                    1 / det * q.Det(0, 2, 3, 1, 5, 6, 2, 7, 8),
                    -1 / det * q.Det(0, 1, 3, 1, 4, 6, 2, 5, 8));
                return q.VertexError(place);
            }

            Vector3d middle = (p1.Position + p2.Position) * 0.5;
            double e1 = q.VertexError(p1.Position), e2 = q.VertexError(p2.Position), e3 = q.VertexError(middle);
            double least = Math.Min(e1, Math.Min(e2, e3));
            place = least == e1 ? p1.Position : least == e2 ? p2.Position : middle;
            return least;
        }
    }

    private sealed class Triangle
    {
        public int[] V = [];                 // the welded point of each corner
        public int[] Source = [];            // each corner's source vertex, for what it carries besides its place
        public double[] Error = new double[4]; // of collapsing edge 0, 1, 2 (corner i to i + 1), and the least of them
        public bool Deleted, Dirty;
        public Vector3d Normal;
    }

    private sealed class Point
    {
        public Vector3d Position;
        public SymmetricMatrix Quadric;
        public int Start, Count;
        public bool Border;
    }

    /// <summary>
    /// A 4x4 symmetric matrix as its 10 distinct entries: the quadric of a plane, summed.
    /// </summary>
    private readonly record struct SymmetricMatrix(double M0, double M1, double M2, double M3, double M4, double M5, double M6, double M7, double M8, double M9)
    {
        /// <summary>
        /// The quadric of the plane ax + by + cz + d = 0.
        /// </summary>
        public SymmetricMatrix(double a, double b, double c, double d)
            : this(a * a, a * b, a * c, a * d, b * b, b * c, b * d, c * c, c * d, d * d)
        {
        }

        private double this[int i] => i switch
        {
            0 => M0, 1 => M1, 2 => M2, 3 => M3, 4 => M4, 5 => M5, 6 => M6, 7 => M7, 8 => M8, _ => M9,
        };

        public static SymmetricMatrix operator +(SymmetricMatrix l, SymmetricMatrix r) =>
            new(l.M0 + r.M0, l.M1 + r.M1, l.M2 + r.M2, l.M3 + r.M3, l.M4 + r.M4, l.M5 + r.M5, l.M6 + r.M6, l.M7 + r.M7, l.M8 + r.M8, l.M9 + r.M9);

        /// <summary>
        /// The determinant of the 3x3 matrix the nine entries make.
        /// </summary>
        public double Det(int a11, int a12, int a13, int a21, int a22, int a23, int a31, int a32, int a33)
        {
            return (this[a11] * this[a22] * this[a33]) + (this[a13] * this[a21] * this[a32]) + (this[a12] * this[a23] * this[a31])
                - (this[a13] * this[a22] * this[a31]) - (this[a11] * this[a23] * this[a32]) - (this[a12] * this[a21] * this[a33]);
        }

        /// <summary>
        /// The squared distance of the point from the planes summed into the matrix.
        /// </summary>
        public double VertexError(Vector3d v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            return (M0 * x * x) + (2 * M1 * x * y) + (2 * M2 * x * z) + (2 * M3 * x) + (M4 * y * y)
                + (2 * M5 * y * z) + (2 * M6 * y) + (M7 * z * z) + (2 * M8 * z) + M9;
        }
    }

    /// <summary>
    /// A double precision vector: the quadrics lose too much in single precision on large meshes.
    /// </summary>
    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public Vector3d(Vector3 v)
            : this(v.X, v.Y, v.Z)
        {
        }

        public static Vector3d operator +(Vector3d l, Vector3d r) => new(l.X + r.X, l.Y + r.Y, l.Z + r.Z);

        public static Vector3d operator -(Vector3d l, Vector3d r) => new(l.X - r.X, l.Y - r.Y, l.Z - r.Z);

        public static Vector3d operator *(Vector3d v, double s) => new(v.X * s, v.Y * s, v.Z * s);

        public static double Dot(Vector3d l, Vector3d r) => (l.X * r.X) + (l.Y * r.Y) + (l.Z * r.Z);

        public static Vector3d Cross(Vector3d l, Vector3d r) => new((l.Y * r.Z) - (l.Z * r.Y), (l.Z * r.X) - (l.X * r.Z), (l.X * r.Y) - (l.Y * r.X));

        public Vector3d Normalized()
        {
            double length = Math.Sqrt(Dot(this, this));
            return length > 0 ? this * (1 / length) : this;
        }

        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    }
}
