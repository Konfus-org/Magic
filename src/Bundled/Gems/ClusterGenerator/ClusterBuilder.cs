using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.ClusterGenerator;

/// <summary>
/// Makes a model's <see cref="ClusterHierarchy"/>, after Karis, "Nanite: A Deep Dive" (SIGGRAPH 2021). The model's
/// triangles are split into clusters, each of one material slot, grown greedily from triangle to neighbouring triangle
/// so a cluster is compact and shares its vertices. Then, level after level, neighbouring clusters are grouped by how much
/// border they share, each group is simplified to half its triangles with its border to the other groups kept where it
/// is (<see cref="QuadricSimplifier.Reduce"/>), and split into clusters again. A group that can no longer simplify is
/// drawn as it is from then on, so its border stays kept at every level after, and what is made beside it still meets
/// it. A group's error is the most of what the simplification moved and what its clusters had, and its bounds enclose
/// theirs, so neither ever shrinks from a level to the next, which is what lets a renderer pick each cluster alone.
/// </summary>
internal static class ClusterBuilder
{
    /// <summary>
    /// The most clusters simplified together: about four come out, as the group is simplified to half.
    /// </summary>
    private const int GroupSize = 8;

    /// <summary>
    /// A group whose simplification keeps more than this share of its triangles has nothing left to give: its clusters
    /// are the coarsest of their part of the model, and drawn however far away it is.
    /// </summary>
    private const float MinReduction = 0.85f;

    private const int MaxLevels = 32;

    /// <summary>
    /// The grid steps from the model's origin to its farthest vertex along an axis: 19 bits and a sign, so a position
    /// packs into 20 bits an axis, and a float on the grid reads back to its step exactly.
    /// </summary>
    private const int PositionSteps = (1 << 19) - 1;

    /// <summary>
    /// <paramref name="model"/>'s clusters at every level, finest to coarsest; null when it has no triangles.
    /// </summary>
    public static ClusterHierarchy? Build(Model model, CancellationToken cancel)
    {
        float step = StepOf(model);
        if (step <= 0)
        {
            return null;
        }

        List<Part> current = [];
        foreach (Mesh mesh in model.Meshes)
        {
            if (mesh.Indices.Length < 3)
            {
                continue;
            }

            Vertex[] vertices = mesh.Vertices.ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i].Position = Snap(vertices[i].Position, step);
            }

            // A triangle whose corners the grid puts in one place has no area left: it is dropped.
            ReadOnlySpan<uint> source = mesh.Indices.Span;
            List<int> indices = [];
            for (int t = 0; t + 2 < source.Length; t += 3)
            {
                (int a, int b, int c) = ((int)source[t], (int)source[t + 1], (int)source[t + 2]);
                if (vertices[a].Position != vertices[b].Position && vertices[b].Position != vertices[c].Position && vertices[a].Position != vertices[c].Position)
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }
            }

            int[] slots = new int[indices.Count / 3];
            Array.Fill(slots, mesh.MaterialSlot);
            current.AddRange(Split(vertices, [.. indices], slots, 0, 0, null));
        }

        if (current.Count == 0)
        {
            return null;
        }

        // The border of a group that stops simplifying is frozen: its clusters are drawn as they are from then on, so
        // what is made beside them at every later level must still meet them there.
        List<Part> all = [];
        HashSet<Vector3> frozen = [];
        for (int level = 0; ; level++)
        {
            cancel.ThrowIfCancellationRequested();
            all.AddRange(current);
            if (current.Count <= 1 || level + 1 >= MaxLevels)
            {
                break;
            }

            List<List<Part>> groups = Group(current);
            HashSet<Vector3>[] borders = BordersOf(groups);
            foreach (HashSet<Vector3> border in borders)
            {
                border.UnionWith(frozen);
            }

            List<Part>?[] made = new List<Part>?[groups.Count];
            Parallel.For(0, groups.Count, new ParallelOptions { CancellationToken = cancel }, g => made[g] = Simplify(groups[g], borders[g], level + 1, step));
            current = [];
            for (int g = 0; g < made.Length; g++)
            {
                if (made[g] is { } parts)
                {
                    current.AddRange(parts);
                }
                else
                {
                    frozen.UnionWith(borders[g]);
                }
            }

            if (current.Count == 0)
            {
                break;
            }
        }

        return Hierarchy(all, step);
    }

    /// <summary>
    /// <paramref name="position"/> on the grid <paramref name="step"/> metres apart from the model's origin.
    /// </summary>
    private static Vector3 Snap(Vector3 position, float step)
    {
        return new Vector3((float)(Math.Round(position.X / (double)step) * step), (float)(Math.Round(position.Y / (double)step) * step), (float)(Math.Round(position.Z / (double)step) * step));
    }

    /// <summary>
    /// The grid's step: the farthest any vertex is from the origin along an axis, over <see cref="PositionSteps"/>.
    /// </summary>
    private static float StepOf(Model model)
    {
        float farthest = 0;
        bool any = false;
        foreach (Mesh mesh in model.Meshes)
        {
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                Vector3 size = Vector3.Abs(vertex.Position);
                farthest = MathF.Max(farthest, MathF.Max(size.X, MathF.Max(size.Y, size.Z)));
                any = true;
            }
        }

        return !any ? 0 : farthest > 0 ? farthest / PositionSteps : 1e-6f;
    }

    /// <summary>
    /// Triangles split into clusters of one material slot each, at most <see cref="ClusterHierarchy.MaxTriangles"/>
    /// triangles and <see cref="ClusterHierarchy.MaxVertices"/> vertices. Each starts from the first triangle left in
    /// Morton order and takes, next, the triangle beside it that brings the fewest new vertices, nearest its middle, so it
    /// stays compact; when none is beside it, the next left in Morton order, which is near.
    /// </summary>
    private static List<Part> Split(Vertex[] vertices, int[] indices, int[] slots, int level, float error, Vector4? errorBounds)
    {
        int count = indices.Length / 3;
        Vector3[] centres = new Vector3[count];
        Dictionary<Vector3, List<int>> touching = [];
        for (int t = 0; t < count; t++)
        {
            Vector3 a = vertices[indices[t * 3]].Position;
            Vector3 b = vertices[indices[(t * 3) + 1]].Position;
            Vector3 c = vertices[indices[(t * 3) + 2]].Position;
            centres[t] = (a + b + c) / 3;
            foreach (Vector3 corner in (ReadOnlySpan<Vector3>)[a, b, c])
            {
                if (!touching.TryGetValue(corner, out List<int>? triangles))
                {
                    triangles = [];
                    touching[corner] = triangles;
                }

                triangles.Add(t);
            }
        }

        int[] order = MortonOrder(centres);
        bool[] taken = new bool[count];
        List<Part> parts = [];
        int cursor = 0;
        while (true)
        {
            while (cursor < order.Length && taken[order[cursor]])
            {
                cursor++;
            }

            if (cursor == order.Length)
            {
                break;
            }

            int slot = slots[order[cursor]];
            List<int> chosen = [];
            Dictionary<int, int> local = [];
            HashSet<int> frontier = [];
            Vector3 sum = Vector3.Zero;
            int next = order[cursor];
            while (next >= 0)
            {
                Take(next);
                if (chosen.Count == ClusterHierarchy.MaxTriangles)
                {
                    break;
                }

                next = Best();
            }

            parts.Add(PartOf(vertices, indices, chosen, local, slot, level, error, errorBounds));

            void Take(int triangle)
            {
                taken[triangle] = true;
                chosen.Add(triangle);
                sum += centres[triangle];
                frontier.Remove(triangle);
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = indices[(triangle * 3) + corner];
                    local.TryAdd(vertex, local.Count);
                    foreach (int beside in touching[vertices[vertex].Position])
                    {
                        if (!taken[beside] && slots[beside] == slot)
                        {
                            frontier.Add(beside);
                        }
                    }
                }
            }

            int Best()
            {
                Vector3 middle = sum / chosen.Count;
                int best = -1;
                (int Added, float Distance) bestScore = (int.MaxValue, float.MaxValue);
                foreach (int triangle in frontier)
                {
                    int added = NewVertices(triangle);
                    if (local.Count + added > ClusterHierarchy.MaxVertices)
                    {
                        continue;
                    }

                    float distance = Vector3.DistanceSquared(centres[triangle], middle);
                    if (added < bestScore.Added || (added == bestScore.Added && distance < bestScore.Distance))
                    {
                        (best, bestScore) = (triangle, (added, distance));
                    }
                }

                if (best >= 0 || frontier.Count > 0)
                {
                    return best;
                }

                // Nothing beside it: the next triangle left in Morton order, near it, of its slot.
                for (int i = cursor; i < order.Length; i++)
                {
                    int triangle = order[i];
                    if (!taken[triangle] && slots[triangle] == slot && local.Count + NewVertices(triangle) <= ClusterHierarchy.MaxVertices)
                    {
                        return triangle;
                    }
                }

                return -1;
            }

            int NewVertices(int triangle)
            {
                int added = 0;
                for (int corner = 0; corner < 3; corner++)
                {
                    added += local.ContainsKey(indices[(triangle * 3) + corner]) ? 0 : 1;
                }

                return added;
            }
        }

        return parts;
    }

    /// <summary>
    /// One cluster: the chosen triangles with their vertices numbered from 0 in the order they were met.
    /// </summary>
    private static Part PartOf(Vertex[] vertices, int[] indices, List<int> chosen, Dictionary<int, int> local, int slot, int level, float error, Vector4? errorBounds)
    {
        Vertex[] own = new Vertex[local.Count];
        foreach ((int vertex, int index) in local)
        {
            own[index] = vertices[vertex];
        }

        byte[] corners = new byte[chosen.Count * 3];
        for (int t = 0; t < chosen.Count; t++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                corners[(t * 3) + corner] = (byte)local[indices[(chosen[t] * 3) + corner]];
            }
        }

        Vector4 bounds = SphereAround(own);
        Vector4 errorSphere = errorBounds ?? bounds;
        return new Part
        {
            Vertices = own,
            Indices = corners,
            Slot = slot,
            Level = level,
            Bounds = bounds,
            ErrorBounds = errorSphere,
            ParentErrorBounds = errorSphere,
            Error = error,
        };
    }

    /// <summary>
    /// Clusters grouped with their neighbours: each group starts from the first cluster left in Morton order and takes,
    /// next, the cluster left that shares the most border with it, up to <see cref="GroupSize"/>; with none beside it,
    /// the next left in Morton order when it is near.
    /// </summary>
    private static List<List<Part>> Group(List<Part> parts)
    {
        Dictionary<Vector3, List<int>> touching = [];
        for (int p = 0; p < parts.Count; p++)
        {
            foreach (Vertex vertex in parts[p].Vertices)
            {
                if (!touching.TryGetValue(vertex.Position, out List<int>? owners))
                {
                    owners = [];
                    touching[vertex.Position] = owners;
                }

                if (owners.Count == 0 || owners[^1] != p)
                {
                    owners.Add(p);
                }
            }
        }

        Dictionary<int, int>[] shared = new Dictionary<int, int>[parts.Count];
        for (int p = 0; p < parts.Count; p++)
        {
            shared[p] = [];
        }

        foreach (List<int> owners in touching.Values)
        {
            for (int i = 0; i < owners.Count; i++)
            {
                for (int j = i + 1; j < owners.Count; j++)
                {
                    shared[owners[i]][owners[j]] = shared[owners[i]].GetValueOrDefault(owners[j]) + 1;
                    shared[owners[j]][owners[i]] = shared[owners[j]].GetValueOrDefault(owners[i]) + 1;
                }
            }
        }

        Vector3[] centres = new Vector3[parts.Count];
        for (int p = 0; p < parts.Count; p++)
        {
            centres[p] = new Vector3(parts[p].Bounds.X, parts[p].Bounds.Y, parts[p].Bounds.Z);
        }

        int[] order = MortonOrder(centres);
        bool[] grouped = new bool[parts.Count];
        List<List<Part>> groups = [];
        for (int o = 0; o < order.Length; o++)
        {
            if (grouped[order[o]])
            {
                continue;
            }

            List<int> members = [order[o]];
            grouped[order[o]] = true;
            Dictionary<int, int> beside = new(shared[order[o]]);
            while (members.Count < GroupSize)
            {
                int best = -1;
                int most = 0;
                foreach ((int part, int border) in beside)
                {
                    if (!grouped[part] && border > most)
                    {
                        (best, most) = (part, border);
                    }
                }

                best = best >= 0 ? best : NearNext(o, members[^1]);
                if (best < 0)
                {
                    break;
                }

                members.Add(best);
                grouped[best] = true;
                foreach ((int part, int border) in shared[best])
                {
                    beside[part] = beside.GetValueOrDefault(part) + border;
                }
            }

            List<Part> group = new(members.Count);
            foreach (int member in members)
            {
                group.Add(parts[member]);
            }

            groups.Add(group);
        }

        return groups;

        // The next cluster left in Morton order, when it is within the two clusters' sizes of the last one taken.
        int NearNext(int from, int last)
        {
            for (int o = from + 1; o < order.Length; o++)
            {
                int part = order[o];
                if (grouped[part])
                {
                    continue;
                }

                float reach = (parts[part].Bounds.W + parts[last].Bounds.W) * 2;
                return Vector3.DistanceSquared(centres[part], centres[last]) <= reach * reach ? part : -1;
            }

            return -1;
        }
    }

    /// <summary>
    /// For each group, the places its clusters share with another group's: its border, which it keeps as it simplifies.
    /// </summary>
    private static HashSet<Vector3>[] BordersOf(List<List<Part>> groups)
    {
        const int Shared = -1;
        Dictionary<Vector3, int> groupAt = [];
        for (int g = 0; g < groups.Count; g++)
        {
            foreach (Part part in groups[g])
            {
                foreach (Vertex vertex in part.Vertices)
                {
                    if (!groupAt.TryGetValue(vertex.Position, out int at))
                    {
                        groupAt[vertex.Position] = g;
                    }
                    else if (at != g)
                    {
                        groupAt[vertex.Position] = Shared;
                    }
                }
            }
        }

        HashSet<Vector3>[] borders = new HashSet<Vector3>[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            borders[g] = [];
            foreach (Part part in groups[g])
            {
                foreach (Vertex vertex in part.Vertices)
                {
                    if (groupAt[vertex.Position] == Shared)
                    {
                        borders[g].Add(vertex.Position);
                    }
                }
            }
        }

        return borders;
    }

    /// <summary>
    /// A group simplified to half its triangles with <paramref name="border"/> kept, and split into the clusters of the
    /// next level; its clusters take what they were simplified into as their parent. Null when it would keep too much
    /// (<see cref="MinReduction"/>): its clusters stay the coarsest there are of their part of the model.
    /// </summary>
    private static List<Part>? Simplify(List<Part> group, HashSet<Vector3> border, int level, float step)
    {
        List<Vertex> vertices = [];
        List<uint> indices = [];
        List<int> slotOf = [];
        float error = 0;
        Vector4 bounds = group[0].ErrorBounds;
        foreach (Part part in group)
        {
            uint first = (uint)vertices.Count;
            vertices.AddRange(part.Vertices);
            foreach (byte index in part.Indices)
            {
                indices.Add(first + index);
            }

            for (int i = 0; i < part.Vertices.Length; i++)
            {
                slotOf.Add(part.Slot);
            }

            error = MathF.Max(error, part.Error);
            bounds = Enclose(bounds, part.ErrorBounds);
        }

        int triangles = indices.Count / 3;
        Mesh soup = new() { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
        QuadricSimplifier.Reduced reduced = QuadricSimplifier.Reduce(soup, triangles / 2, border);
        ReadOnlySpan<uint> kept = reduced.Mesh.Indices.Span;
        if (kept.Length / 3 > triangles * MinReduction)
        {
            return null;
        }

        error = MathF.Max(error, reduced.Error);
        foreach (Part part in group)
        {
            part.ParentError = error;
            part.ParentErrorBounds = bounds;
        }

        // Back on the grid: where a collapse put a point is off it, and two clusters that share it snap it alike.
        Vertex[] made = reduced.Mesh.Vertices.ToArray();
        for (int i = 0; i < made.Length; i++)
        {
            made[i].Position = Snap(made[i].Position, step);
        }

        List<int> madeIndices = [];
        List<int> slots = [];
        for (int t = 0; t + 2 < kept.Length; t += 3)
        {
            (int a, int b, int c) = ((int)kept[t], (int)kept[t + 1], (int)kept[t + 2]);
            if (made[a].Position == made[b].Position || made[b].Position == made[c].Position || made[a].Position == made[c].Position)
            {
                continue;
            }

            madeIndices.Add(a);
            madeIndices.Add(b);
            madeIndices.Add(c);
            slots.Add(slotOf[reduced.Sources[a]]);
        }

        return Split(made, [.. madeIndices], [.. slots], level, error, bounds);
    }

    /// <summary>
    /// The parts as Core's hierarchy, coarsest level last; those never simplified into anything have no parent.
    /// </summary>
    private static ClusterHierarchy Hierarchy(List<Part> parts, float step)
    {
        parts.Sort(static (left, right) => left.Level.CompareTo(right.Level));
        List<Vertex> vertices = [];
        List<byte> indices = [];
        Cluster[] clusters = new Cluster[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            Part part = parts[i];
            clusters[i] = new Cluster
            {
                FirstVertex = vertices.Count,
                VertexCount = part.Vertices.Length,
                FirstIndex = indices.Count,
                TriangleCount = part.Indices.Length / 3,
                MaterialSlot = part.Slot,
                Level = part.Level,
                Bounds = part.Bounds,
                ErrorBounds = part.ErrorBounds,
                Error = part.Error,
                ParentErrorBounds = part.ParentErrorBounds,
                ParentError = part.ParentError,
            };
            vertices.AddRange(part.Vertices);
            indices.AddRange(part.Indices);
        }

        return new ClusterHierarchy { Vertices = vertices.ToArray(), Indices = indices.ToArray(), Clusters = clusters, PositionStep = step };
    }

    /// <summary>
    /// The sphere around <paramref name="vertices"/>: the middle of their box, out to the farthest.
    /// </summary>
    internal static Vector4 SphereAround(ReadOnlySpan<Vertex> vertices)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        Vector3 centre = (min + max) / 2;
        float radius = 0;
        foreach (Vertex vertex in vertices)
        {
            radius = MathF.Max(radius, Vector3.Distance(centre, vertex.Position));
        }

        return new Vector4(centre, radius);
    }

    /// <summary>
    /// The smallest sphere around both spheres.
    /// </summary>
    private static Vector4 Enclose(Vector4 first, Vector4 second)
    {
        Vector3 a = new(first.X, first.Y, first.Z);
        Vector3 b = new(second.X, second.Y, second.Z);
        float distance = Vector3.Distance(a, b);
        if (distance + second.W <= first.W)
        {
            return first;
        }

        if (distance + first.W <= second.W)
        {
            return second;
        }

        float radius = (distance + first.W + second.W) / 2;
        Vector3 centre = a + ((b - a) * ((radius - first.W) / distance));
        return new Vector4(centre, radius);
    }

    /// <summary>
    /// The indices of <paramref name="points"/> in Morton order: along a curve through their box that keeps what is near
    /// in space near in the order.
    /// </summary>
    private static int[] MortonOrder(Vector3[] points)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vector3 point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        Vector3 scale = new Vector3(1023) / Vector3.Max(max - min, new Vector3(1e-12f));
        ulong[] codes = new ulong[points.Length];
        int[] order = new int[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 cell = (points[i] - min) * scale;
            codes[i] = (Spread((uint)cell.X) << 2) | (Spread((uint)cell.Y) << 1) | Spread((uint)cell.Z);
            order[i] = i;
        }

        Array.Sort(codes, order);
        return order;

        // Ten bits spread two apart, so three axes interleave.
        static ulong Spread(uint value)
        {
            ulong bits = value & 1023;
            bits = (bits | (bits << 16)) & 0x030000FF;
            bits = (bits | (bits << 8)) & 0x0300F00F;
            bits = (bits | (bits << 4)) & 0x030C30C3;
            return (bits | (bits << 2)) & 0x09249249;
        }
    }

    /// <summary>
    /// A cluster being made: its own vertices and triangles, and what picking it takes.
    /// </summary>
    private sealed class Part
    {
        public required Vertex[] Vertices { get; init; }

        public required byte[] Indices { get; init; }

        public required int Slot { get; init; }

        public required int Level { get; init; }

        public required Vector4 Bounds { get; init; }

        public required Vector4 ErrorBounds { get; init; }

        public required float Error { get; init; }

        /// <summary>
        /// The error bounds of what it was simplified into; its own until it is.
        /// </summary>
        public required Vector4 ParentErrorBounds { get; set; }

        public float ParentError { get; set; } = float.PositiveInfinity;
    }
}
