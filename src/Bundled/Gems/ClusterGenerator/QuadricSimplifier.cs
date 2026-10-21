using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;

namespace Magic.ClusterGenerator;

/// <summary>
/// Sven Forstmann's fast quadric mesh simplification: Garland and Heckbert's quadric error metrics, but instead of a
/// priority queue of edge collapses each pass collapses every edge whose error is under a threshold that grows from
/// pass to pass, so it runs in near linear time. Reference:
/// https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification (MIT), and Garland and Heckbert, "Surface
/// Simplification Using Quadric Error Metrics", SIGGRAPH 1997.
/// <para>
/// The collapses work on positions only: vertices at the same place are welded first, so a uv or normal seam (a
/// cylinder's cap, a cube's edges) is not a border that cannot move. A corner keeps the uv of the vertex it had, so the
/// seams come out where they were with the corners moved, and its material. Its normal follows its new place: the
/// corners shaded smoothly across each other (those of one triangle, and those at one place with one normal) take
/// the normal of the triangles they are now the corners of, so what it makes is shaded as the model is, flat where it
/// was flat, smooth where it was smooth, hard where it had an edge, rather than each triangle keeping the normal of
/// the spot it started at, which shows as facets on a smooth model once the triangles have grown.
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
    /// The mesh brought down to about <paramref name="target"/> triangles, however far that moves it, with every vertex
    /// at a place in <paramref name="locked"/> kept where it is: a group of clusters simplified with its border kept,
    /// so it still meets the clusters around it. The mesh's material slot means nothing here; each vertex made says
    /// which of the mesh's it carries (<see cref="Reduced.Sources"/>), so a caller that keeps a material by vertex
    /// keeps it by triangle. The error is how far the surface may have moved, in the mesh's units.
    /// </summary>
    internal static Reduced Reduce(Mesh mesh, int target, IReadOnlySet<Vector3> locked)
    {
        Simplification work = new(mesh, locked);
        work.Run(Math.Max(1, target));
        Mesh reduced = work.ToMesh(out int[] sources);
        return new Reduced(reduced, sources, (float)Math.Sqrt(work.MaxError));
    }

    /// <summary>
    /// A mesh <see cref="Reduce"/> brought down, the source vertex each of its vertices carries everything but its
    /// place from, and how far its surface may have moved.
    /// </summary>
    internal readonly record struct Reduced(Mesh Mesh, int[] Sources, float Error);

    /// <summary>
    /// A 4x4 symmetric matrix as its 10 distinct entries: the quadric of a plane, summed.
    /// </summary>
    private readonly record struct SymmetricMatrix(
        double M0,
        double M1,
        double M2,
        double M3,
        double M4,
        double M5,
        double M6,
        double M7,
        double M8,
        double M9)
    {
        /// <summary>
        /// The quadric of the plane ax + by + cz + d = 0.
        /// </summary>
        public SymmetricMatrix(double a, double b, double c, double d)
            : this(a * a, a * b, a * c, a * d, b * b, b * c, b * d, c * c, c * d, d * d)
        {
        }

        private double this[int entry] => entry switch
        {
            0 => M0,
            1 => M1,
            2 => M2,
            3 => M3,
            4 => M4,
            5 => M5,
            6 => M6,
            7 => M7,
            8 => M8,
            _ => M9,
        };

        public static SymmetricMatrix operator +(SymmetricMatrix left, SymmetricMatrix right)
        {
            return new(
                left.M0 + right.M0,
                left.M1 + right.M1,
                left.M2 + right.M2,
                left.M3 + right.M3,
                left.M4 + right.M4,
                left.M5 + right.M5,
                left.M6 + right.M6,
                left.M7 + right.M7,
                left.M8 + right.M8,
                left.M9 + right.M9);
        }

        /// <summary>
        /// The determinant of the 3x3 matrix the nine entries make.
        /// </summary>
        internal double Det(int a11, int a12, int a13, int a21, int a22, int a23, int a31, int a32, int a33)
        {
            return (this[a11] * this[a22] * this[a33]) + (this[a13] * this[a21] * this[a32])
                + (this[a12] * this[a23] * this[a31])
                - (this[a13] * this[a22] * this[a31]) - (this[a11] * this[a23] * this[a32]) - (this[a12] * this[a21]
                    * this[a33]);
        }

        /// <summary>
        /// The squared distance of the point from the planes summed into the matrix.
        /// </summary>
        internal double VertexError(Vector3 point)
        {
            double x = point.X;
            double y = point.Y;
            double z = point.Z;
            return (M0 * x * x) + (2 * M1 * x * y) + (2 * M2 * x * z) + (2 * M3 * x) + (M4 * y * y)
                + (2 * M5 * y * z) + (2 * M6 * y) + (M7 * z * z) + (2 * M8 * z) + M9;
        }
    }

    /// <summary>
    /// One mesh being simplified: its welded points, its triangles, and each point's references to the triangle corners
    /// it is.
    /// </summary>
    private sealed class Simplification
    {
        private readonly Mesh _source;
        private readonly List<Triangle> _triangles = [];
        private readonly List<Point> _points = [];
        private readonly List<(int Triangle, int Corner)> _refs = [];

        /// <summary>
        /// The source vertex each moving corner takes in a collapse, by the one it had (<see cref="SharedSources"/>).
        /// </summary>
        private readonly Dictionary<int, int> _sources = [];

        /// <summary>
        /// Which source vertices are shaded smoothly across each other, as sets (union-find, each entry its parent):
        /// two at one place with one normal, a uv seam, from the start, and the two ends of every triangle a collapse
        /// deletes, which was one smooth triangle (<see cref="JoinAcross"/>). Those across a hard edge are never
        /// joined, as no triangle has both. A vertex that moved takes its normal from its set (<see cref="ToMesh"/>).
        /// </summary>
        private readonly int[] _smooth;
        private bool[] _keptDeleted = [];

        private bool[] _goneDeleted = [];

        public Simplification(Mesh source, IReadOnlySet<Vector3> locked)
        {
            _source = source;

            // Weld by position: the welded points are what collapses, each corner keeps its source vertex for the rest.
            ReadOnlySpan<Vertex> vertices = source.Vertices.Span;
            ReadOnlySpan<uint> indices = source.Indices.Span;
            Dictionary<Vector3, int> pointOf = [];
            Dictionary<(Vector3, Vector3), int> sameAt = [];
            int[] weld = new int[vertices.Length];
            _smooth = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 position = vertices[i].Position;
                if (!pointOf.TryGetValue(position, out int point))
                {
                    point = _points.Count;
                    pointOf[position] = point;
                    _points.Add(new Point { Position = position, Locked = locked.Contains(position) });
                }

                weld[i] = point;
                _smooth[i] = i;
                if (!sameAt.TryAdd((position, vertices[i].Normal), i)) Join(i, sameAt[(position, vertices[i].Normal)]);
            }

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int first = (int)indices[i];
                int second = (int)indices[i + 1];
                int third = (int)indices[i + 2];
                Triangle triangle = new()
                {
                    Points = [weld[first], weld[second], weld[third]],
                    Source = [first, second, third]
                };
                if (triangle.Points[0] != triangle.Points[1] && triangle.Points[1] != triangle.Points[2]
                    && triangle.Points[0] != triangle.Points[2])
                {
                    _triangles.Add(triangle);
                }
            }
        }

        /// <summary>
        /// The most error of any collapse made, a squared distance, as the quadric error is a sum of squared distances.
        /// </summary>
        internal double MaxError { get; private set; }

        /// <summary>
        /// Collapses edges, pass by pass, until <paramref name="target"/> triangles are left, or the passes run out.
        /// </summary>
        internal void Run(int target)
        {
            int deleted = 0;
            int count = _triangles.Count;
            for (int pass = 0; pass < MaxPasses && count - deleted > target; pass++)
            {
                if (pass % 5 == 0) Update(pass);

                foreach (Triangle triangle in _triangles)
                {
                    triangle.Dirty = false;
                }

                double threshold = 0.000000001 * Math.Pow(pass + 3, Aggressiveness);
                for (int t = 0; t < _triangles.Count && count - deleted > target; t++)
                {
                    Triangle triangle = _triangles[t];
                    if (triangle.Error[3] > threshold || triangle.Deleted || triangle.Dirty) continue;

                    for (int edge = 0; edge < 3; edge++)
                    {
                        if (triangle.Error[edge] > threshold || !TryCollapseEdge(triangle, edge, out int removed))
                        {
                            continue;
                        }

                        deleted += removed;
                        break;
                    }
                }
            }

            _triangles.RemoveAll(static triangle => triangle.Deleted);
        }

        /// <summary>
        /// The mesh as it is now: one vertex per (point, source vertex) pair a corner uses, the source vertex at the
        /// point's place, and which source vertex each is. At a point that is not locked where a corner has come from
        /// elsewhere, the vertices of each smooth set (<see cref="_smooth"/>) take the normal of the triangles the set
        /// is the corners of, summed by their area, and their tangent is laid flat to it; a locked point keeps its
        /// source normals, as the clusters either side of a border must carry the same normals along it.
        /// </summary>
        internal Mesh ToMesh(out int[] sources)
        {
            ReadOnlySpan<Vertex> sourceVertices = _source.Vertices.Span;
            Dictionary<(int Point, int Source), uint> made = [];
            List<Vertex> vertices = [];
            List<int> madeFrom = [];
            List<(int Point, int Set)> madeAt = [];
            List<uint> indices = [];
            Dictionary<(int Point, int Set), (Vector3 Normal, bool Moved)> sets = [];
            foreach (Triangle triangle in _triangles)
            {
                if (triangle.Deleted) continue;

                Vector3 first = _points[triangle.Points[0]].Position;
                Vector3 area = Vector3.Cross(
                    _points[triangle.Points[1]].Position - first,
                    _points[triangle.Points[2]].Position - first);
                for (int corner = 0; corner < 3; corner++)
                {
                    (int point, int source) = (triangle.Points[corner], triangle.Source[corner]);
                    (int, int) key = (point, Find(source));
                    if (!made.TryGetValue((point, source), out uint index))
                    {
                        index = (uint)vertices.Count;
                        vertices.Add(sourceVertices[source] with { Position = _points[point].Position });
                        madeFrom.Add(source);
                        madeAt.Add(key);
                        made[(point, source)] = index;
                    }

                    (Vector3 normal, bool moved) = sets.GetValueOrDefault(key);
                    sets[key] = (normal + area, moved || sourceVertices[source].Position != _points[point].Position);
                    indices.Add(index);
                }
            }

            for (int i = 0; i < vertices.Count; i++)
            {
                (Vector3 sum, bool moved) = sets[madeAt[i]];
                if (!moved || _points[madeAt[i].Point].Locked || sum.Length() <= 0) continue;

                Vector3 normal = Vector3.Normalize(sum);
                Vertex vertex = vertices[i];
                Vector3 along = new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
                along -= normal * Vector3.Dot(normal, along);
                Vector4 tangent = along.LengthSquared() > 0
                    ? new Vector4(Vector3.Normalize(along), vertex.Tangent.W)
                    : vertex.Tangent;
                vertices[i] = vertex with { Normal = normal, Tangent = tangent };
            }

            sources = [.. madeFrom];
            return new Mesh { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
        }

        private static (int, int) EdgeKey(int from, int to) => from < to ? (from, to) : (to, from);

        /// <summary>
        /// A unit vector along <paramref name="vector"/>, or zero for a zero one: a degenerate triangle's normal, whose
        /// plane then adds nothing to a quadric.
        /// </summary>
        private static Vector3 Normalized(Vector3 vector)
        {
            float length = vector.Length();
            return length > 0 ? vector / length : Vector3.Zero;
        }

        /// <summary>
        /// Collapses the triangle's edge when the collapse is sound: its first end stays, at the place the collapse
        /// puts it, and its second goes into it; <paramref name="deleted"/> is how many triangles went with it. An edge
        /// along a border collapses along it, one across would pull the border in, and a collapse that flips a triangle
        /// is refused.
        /// </summary>
        private bool TryCollapseEdge(Triangle triangle, int edge, out int deleted)
        {
            deleted = 0;
            int keptId = triangle.Points[edge];
            int goneId = triangle.Points[(edge + 1) % 3];
            Point kept = _points[keptId];
            Point gone = _points[goneId];
            if (kept.Border != gone.Border) return false;

            ComputeCollapseError(keptId, goneId, out Vector3 place);
            if (_keptDeleted.Length < kept.Count) Array.Resize(ref _keptDeleted, kept.Count * 2);
            if (_goneDeleted.Length < gone.Count) Array.Resize(ref _goneDeleted, gone.Count * 2);
            if (IsFlipped(place, goneId, kept, _keptDeleted) || IsFlipped(place, keptId, gone, _goneDeleted))
            {
                return false;
            }

            bool ontoKept = place == kept.Position;
            bool ontoGone = !ontoKept && place == gone.Position;
            JoinAcross(goneId, kept, _keptDeleted);
            if (ontoKept || ontoGone) SharedSources(goneId, kept, _keptDeleted, ontoKept);

            MaxError = Math.Max(MaxError, triangle.Error[edge]);
            kept.Position = place;
            kept.Quadric += gone.Quadric;
            kept.Locked |= gone.Locked;
            int start = _refs.Count;
            deleted += Retarget(keptId, kept, _keptDeleted, ontoGone);
            deleted += Retarget(keptId, gone, _goneDeleted, ontoKept);
            MoveReferences(kept, start);
            return true;
        }

        /// <summary>
        /// The references added for the kept point, from <paramref name="start"/> on, become its run: moved into the
        /// room its old run had when they fit, so the list does not grow, else left where they were added.
        /// </summary>
        private void MoveReferences(Point kept, int start)
        {
            int added = _refs.Count - start;
            if (added <= kept.Count)
            {
                for (int r = 0; r < added; r++)
                {
                    _refs[kept.Start + r] = _refs[start + r];
                }

                _refs.RemoveRange(start, added);
            }
            else
            {
                kept.Start = start;
            }

            kept.Count = added;
        }

        /// <summary>
        /// The set of source vertex <paramref name="vertex"/> in <see cref="_smooth"/>, flattening the path to it.
        /// </summary>
        private int Find(int vertex)
        {
            while (_smooth[vertex] != vertex)
            {
                _smooth[vertex] = _smooth[_smooth[vertex]];
                vertex = _smooth[vertex];
            }

            return vertex;
        }

        private void Join(int first, int second)
        {
            _smooth[Find(first)] = Find(second);
        }

        /// <summary>
        /// Joins the sets of the corners at <paramref name="point"/> and at <paramref name="other"/> of each triangle
        /// the collapse deletes: it was one smooth triangle, so its ends shade smoothly across each other where they
        /// now meet.
        /// </summary>
        private void JoinAcross(int other, Point point, bool[] deleted)
        {
            for (int r = 0; r < point.Count; r++)
            {
                (int index, int corner) = _refs[point.Start + r];
                Triangle triangle = _triangles[index];
                if (triangle.Deleted || !deleted[r]) continue;

                int otherCorner = triangle.Points[(corner + 1) % 3] == other ? (corner + 1) % 3 : (corner + 2) % 3;
                Join(triangle.Source[corner], triangle.Source[otherCorner]);
            }
        }

        /// <summary>
        /// Whether moving the point at the other end of <paramref name="other"/>'s edge to <paramref name="place"/>
        /// would turn one of <paramref name="point"/>'s triangles over (or make it a sliver); marks those that would
        /// lose the edge, which the collapse deletes.
        /// </summary>
        private bool IsFlipped(Vector3 place, int other, Point point, bool[] deleted)
        {
            for (int r = 0; r < point.Count; r++)
            {
                (int index, int corner) = _refs[point.Start + r];
                Triangle triangle = _triangles[index];
                if (triangle.Deleted) continue;

                int next = triangle.Points[(corner + 1) % 3];
                int last = triangle.Points[(corner + 2) % 3];
                if (next == other || last == other)
                {
                    deleted[r] = true;
                    continue;
                }

                Vector3 toNext = Normalized(_points[next].Position - place);
                Vector3 toLast = Normalized(_points[last].Position - place);
                if (MathF.Abs(Vector3.Dot(toNext, toLast)) > 0.999f) return true;

                Vector3 normal = Normalized(Vector3.Cross(toNext, toLast));
                deleted[r] = false;
                if (Vector3.Dot(normal, triangle.Normal) < 0.2f) return true;
            }

            return false;
        }

        /// <summary>
        /// When an edge collapses onto one of its ends, the corners of the other end are moved to where that end is,
        /// and should carry what it carries there: across each triangle the collapse deletes (which has both ends), the
        /// moving end's source vertex is mapped to the staying end's, into <see cref="_sources"/>. Without it a corner
        /// keeps its own uv where it has moved to, and the texture stretches over the triangles it is part of.
        /// <paramref name="ontoPoint"/> when the collapse is onto <paramref name="point"/>, rather than onto
        /// <paramref name="other"/>.
        /// </summary>
        private void SharedSources(int other, Point point, bool[] deleted, bool ontoPoint)
        {
            _sources.Clear();
            for (int r = 0; r < point.Count; r++)
            {
                (int index, int corner) = _refs[point.Start + r];
                Triangle triangle = _triangles[index];
                if (triangle.Deleted || !deleted[r]) continue;

                int otherCorner = triangle.Points[(corner + 1) % 3] == other ? (corner + 1) % 3 : (corner + 2) % 3;
                if (ontoPoint)
                {
                    _sources[triangle.Source[otherCorner]] = triangle.Source[corner];
                }
                else
                {
                    _sources[triangle.Source[corner]] = triangle.Source[otherCorner];
                }
            }
        }

        /// <summary>
        /// Points <paramref name="point"/>'s triangles at <paramref name="into"/>, deleting those the collapse removed;
        /// the references go on the end of the list. When <paramref name="moved"/>, the point's corners now stand where
        /// the other end stood and take its source vertices (<see cref="SharedSources"/>). Answers how many were
        /// deleted.
        /// </summary>
        private int Retarget(int into, Point point, bool[] deleted, bool moved)
        {
            int removed = 0;
            for (int r = 0; r < point.Count; r++)
            {
                (int index, int corner) = _refs[point.Start + r];
                Triangle triangle = _triangles[index];
                if (triangle.Deleted) continue;

                if (deleted[r])
                {
                    triangle.Deleted = true;
                    removed++;
                    continue;
                }

                triangle.Points[corner] = into;
                if (moved && _sources.TryGetValue(triangle.Source[corner], out int source))
                {
                    triangle.Source[corner] = source;
                }

                triangle.Dirty = true;
                SetErrors(triangle);
                _refs.Add((index, corner));
            }

            return removed;
        }

        /// <summary>
        /// Compacts the triangles and rebuilds the references; the first time, also the quadrics, the errors and the
        /// borders.
        /// </summary>
        private void Update(int pass)
        {
            _triangles.RemoveAll(static triangle => triangle.Deleted);
            IndexReferences();
            if (pass != 0) return;

            MarkBorders();
            AddBorderPlanes(AddPlaneQuadrics());
            foreach (Triangle triangle in _triangles)
            {
                SetErrors(triangle);
            }
        }

        /// <summary>
        /// Each point's run in <see cref="_refs"/>: the triangles that use it, and at which corner.
        /// </summary>
        private void IndexReferences()
        {
            foreach (Point point in _points)
            {
                point.Count = 0;
            }

            foreach (Triangle triangle in _triangles)
            {
                _points[triangle.Points[0]].Count++;
                _points[triangle.Points[1]].Count++;
                _points[triangle.Points[2]].Count++;
            }

            int start = 0;
            foreach (Point point in _points)
            {
                point.Start = start;
                start += point.Count;
                point.Count = 0;
            }

            _refs.Clear();
            CollectionsMarshal.SetCount(_refs, start);
            for (int t = 0; t < _triangles.Count; t++)
            {
                Triangle triangle = _triangles[t];
                for (int corner = 0; corner < 3; corner++)
                {
                    Point point = _points[triangle.Points[corner]];
                    _refs[point.Start + point.Count] = (t, corner);
                    point.Count++;
                }
            }
        }

        /// <summary>
        /// A border is an edge with one triangle: its points only collapse into each other, or the mesh would shrink
        /// away from it.
        /// </summary>
        private void MarkBorders()
        {
            foreach (Point point in _points)
            {
                foreach ((int id, int count) in CountNeighbourUses(point))
                {
                    if (count == 1) _points[id].Border = true;
                }
            }
        }

        /// <summary>
        /// How many of the point's triangles each point among their corners is in, the point itself included.
        /// </summary>
        private Dictionary<int, int> CountNeighbourUses(Point point)
        {
            Dictionary<int, int> uses = [];
            for (int r = 0; r < point.Count; r++)
            {
                Triangle triangle = _triangles[_refs[point.Start + r].Triangle];
                for (int corner = 0; corner < 3; corner++)
                {
                    int id = triangle.Points[corner];
                    uses[id] = uses.GetValueOrDefault(id) + 1;
                }
            }

            return uses;
        }

        /// <summary>
        /// Each triangle's plane, as the quadric its three points sum, and its normal; and how many triangles share
        /// each edge, which says which edges are borders.
        /// </summary>
        private Dictionary<(int, int), int> AddPlaneQuadrics()
        {
            Dictionary<(int, int), int> edgeUses = [];
            foreach (Triangle triangle in _triangles)
            {
                Vector3 firstCorner = _points[triangle.Points[0]].Position;
                Vector3 normal = Normalized(Vector3.Cross(
                    _points[triangle.Points[1]].Position - firstCorner,
                    _points[triangle.Points[2]].Position - firstCorner));
                triangle.Normal = normal;
                SymmetricMatrix plane = new(normal.X, normal.Y, normal.Z, -Vector3.Dot(normal, firstCorner));
                _points[triangle.Points[0]].Quadric += plane;
                _points[triangle.Points[1]].Quadric += plane;
                _points[triangle.Points[2]].Quadric += plane;
                for (int edge = 0; edge < 3; edge++)
                {
                    (int, int) key = EdgeKey(triangle.Points[edge], triangle.Points[(edge + 1) % 3]);
                    edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
                }
            }

            return edgeUses;
        }

        /// <summary>
        /// Garland and Heckbert's border planes: through each border edge, standing up from its triangle. A flat
        /// panel's corners sit on two of them, so sliding one along the other edge costs its distance from the first,
        /// and the error cap keeps the corner; points along a straight border still collapse into each other for
        /// nothing.
        /// </summary>
        private void AddBorderPlanes(Dictionary<(int, int), int> edgeUses)
        {
            foreach (Triangle triangle in _triangles)
            {
                for (int edge = 0; edge < 3; edge++)
                {
                    int from = triangle.Points[edge];
                    int to = triangle.Points[(edge + 1) % 3];
                    if (edgeUses[EdgeKey(from, to)] != 1) continue;

                    Vector3 edgeStart = _points[from].Position;
                    Vector3 normal = Normalized(Vector3.Cross(_points[to].Position - edgeStart, triangle.Normal));
                    SymmetricMatrix plane = new(normal.X, normal.Y, normal.Z, -Vector3.Dot(normal, edgeStart));
                    _points[from].Quadric += plane;
                    _points[to].Quadric += plane;
                }
            }
        }

        /// <summary>
        /// The error of collapsing each of the triangle's edges, and the least of them.
        /// </summary>
        private void SetErrors(Triangle triangle)
        {
            for (int edge = 0; edge < 3; edge++)
            {
                triangle.Error[edge]
                    = ComputeCollapseError(triangle.Points[edge], triangle.Points[(edge + 1) % 3], out _);
            }

            triangle.Error[3] = Math.Min(triangle.Error[0], Math.Min(triangle.Error[1], triangle.Error[2]));
        }

        /// <summary>
        /// The error of collapsing the edge, and the place the point goes: where the summed quadric is least, or the
        /// best of the two ends and their middle when that cannot be solved (or an end is on a border).
        /// </summary>
        private double ComputeCollapseError(int firstId, int secondId, out Vector3 place)
        {
            Point first = _points[firstId];
            Point second = _points[secondId];
            SymmetricMatrix quadric = first.Quadric + second.Quadric;
            if (first.Locked || second.Locked)
            {
                // A kept point stays where it is: the other goes into it, and two kept points never meet.
                place = first.Locked ? first.Position : second.Position;
                return first.Locked && second.Locked ? double.PositiveInfinity : quadric.VertexError(place);
            }

            bool border = first.Border && second.Border;
            double det = quadric.Det(0, 1, 2, 1, 4, 5, 2, 5, 7);
            if (det != 0 && !border)
            {
                place = new Vector3(
                    (float)(-1 / det * quadric.Det(1, 2, 3, 4, 5, 6, 5, 7, 8)),
                    (float)(1 / det * quadric.Det(0, 2, 3, 1, 5, 6, 2, 7, 8)),
                    (float)(-1 / det * quadric.Det(0, 1, 3, 1, 4, 6, 2, 5, 8)));
                return quadric.VertexError(place);
            }

            Vector3 middle = (first.Position + second.Position) * 0.5f;
            double atFirst = quadric.VertexError(first.Position);
            double atSecond = quadric.VertexError(second.Position);
            double atMiddle = quadric.VertexError(middle);
            double least = Math.Min(atFirst, Math.Min(atSecond, atMiddle));
            place = least == atFirst ? first.Position : least == atSecond ? second.Position : middle;
            return least;
        }
    }

    /// <summary>
    /// A triangle being simplified.
    /// </summary>
    private sealed class Triangle
    {
        /// <summary>
        /// The welded point of each corner.
        /// </summary>
        internal int[] Points { get; init; } = [];

        /// <summary>
        /// Each corner's source vertex, for what it carries besides its place.
        /// </summary>
        internal int[] Source { get; init; } = [];

        /// <summary>
        /// The error of collapsing edge 0, 1 and 2 (corner i to i + 1), and the least of them.
        /// </summary>
        internal double[] Error { get; } = new double[4];

        internal bool Deleted { get; set; }

        /// <summary>
        /// A collapse this pass changed it, so it waits for the next.
        /// </summary>
        internal bool Dirty { get; set; }

        internal Vector3 Normal { get; set; }
    }

    /// <summary>
    /// The vertices at one place, welded: where they are, the planes of their triangles summed, where their references
    /// start and how many there are, and whether they are on a border.
    /// </summary>
    private sealed class Point
    {
        internal Vector3 Position { get; set; }

        internal SymmetricMatrix Quadric { get; set; }

        internal int Start { get; set; }

        internal int Count { get; set; }

        internal bool Border { get; set; }

        /// <summary>
        /// It stays where it is: on the border of a group of clusters, which the clusters around it meet.
        /// </summary>
        internal bool Locked { get; set; }
    }
}
