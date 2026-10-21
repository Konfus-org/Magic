using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;

namespace Magic.ClusterGenerator;

/// <summary>
/// Makes a model's <see cref="ClusterHierarchy"/>, after Karis, "Nanite: A Deep Dive" (SIGGRAPH 2021), on
/// meshoptimizer (<see cref="Meshopt"/>). Every level indexes one array of the model's own vertices, put on the grid
/// once. The model's triangles are split into compact clusters, each of one material slot, a large mesh in chunks along
/// a spatial sort, every chunk at once. Then, level after level, neighbouring clusters are grouped, each group is
/// simplified to half its triangles with every place it shares with another group locked, and split into clusters
/// again, every group at once. The simplifier keeps a subset of the vertices as they were, so every position stays on
/// the grid and every vertex keeps its normal and uv. A group that can no longer simplify is drawn as it is from then
/// on, so its border stays locked at every level after, and what is made beside it still meets it. A group's error is
/// the most of what the simplification moved and what its clusters had, and its bounds enclose theirs, so neither ever
/// shrinks from a level to the next, which is what lets a renderer pick each cluster alone.
/// </summary>
internal static class ClusterBuilder
{
    /// <summary>
    /// The clusters grouped to be simplified together: about four come out, as the group is simplified to half.
    /// meshoptimizer makes a group up to a third larger where that keeps it whole.
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
    /// The fewest triangles a cluster is cut to when its triangles spread too far for one, so one cut for its spread is
    /// still a third full.
    /// </summary>
    private const int MinTriangles = ClusterHierarchy.MaxTriangles / 3;

    /// <summary>
    /// How far past the size of the clusters made so far a cluster may spread before it is cut, in meshoptimizer's
    /// terms.
    /// </summary>
    private const float SplitFactor = 2;

    /// <summary>
    /// The most triangles of a mesh split into its finest clusters on one worker: a larger mesh is cut into chunks of
    /// this many along a spatial sort, so each chunk is one piece of it, and the chunks are split at once.
    /// </summary>
    private const int ChunkTriangles = 1 << 16;

    /// <summary>
    /// <paramref name="model"/>'s clusters at every level, finest to coarsest; null when it has no triangles.
    /// </summary>
    internal static ClusterHierarchy? Build(Model model, CancellationToken cancel)
    {
        float step = StepOf(model);
        if (step <= 0) return null;

        (Vertex[] vertices, int[] slots, List<uint[]> meshes) = Gather(model, step);
        List<Part> current = SplitFinest(vertices, slots, meshes, cancel);
        if (current.Count == 0) return null;

        // The border of a group that stops simplifying is frozen: its clusters are drawn as they are from then on, so
        // what is made beside them at every later level must still meet them there.
        uint[] places = Weld(vertices);
        bool[] frozen = new bool[vertices.Length];
        List<Part> all = [];
        for (int level = 0; ; level++)
        {
            cancel.ThrowIfCancellationRequested();
            all.AddRange(current);
            if (current.Count <= 1 || level + 1 >= MaxLevels) break;

            List<List<Part>> groups = Group(current, vertices, places);
            byte[] locks = LockBorders(groups, places, frozen);
            List<Part>?[] made = new List<Part>?[groups.Count];
            Parallel.For(
                0,
                groups.Count,
                new ParallelOptions { CancellationToken = cancel },
                g => made[g] = Simplify(groups[g], vertices, slots, locks, level + 1));
            current = [];
            for (int g = 0; g < made.Length; g++)
            {
                if (made[g] is { } parts)
                {
                    current.AddRange(parts);
                }
                else
                {
                    Freeze(groups[g], places, locks, frozen);
                }
            }

            if (current.Count == 0) break;
        }

        return BuildHierarchy(all, vertices, step);
    }

    /// <summary>
    /// The sphere around <paramref name="vertices"/>: the middle of their box, out to the farthest.
    /// </summary>
    internal static Vector4 ComputeSphere(ReadOnlySpan<Vertex> vertices)
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
    /// The model's vertices as the one array every level indexes, on the grid <paramref name="step"/> metres apart,
    /// with each one's material slot, and each mesh's triangles into it. A triangle whose corners the grid puts in one
    /// place has no area left: it is dropped.
    /// </summary>
    private static (Vertex[] Vertices, int[] Slots, List<uint[]> Meshes) Gather(Model model, float step)
    {
        int count = 0;
        foreach (Mesh mesh in model.Meshes)
        {
            count += mesh.Vertices.Length;
        }

        Vertex[] vertices = new Vertex[count];
        int[] slots = new int[count];
        List<uint[]> meshes = [];
        int first = 0;
        foreach (Mesh mesh in model.Meshes)
        {
            ReadOnlySpan<Vertex> own = mesh.Vertices.Span;
            for (int i = 0; i < own.Length; i++)
            {
                vertices[first + i] = own[i] with { Position = Snap(own[i].Position, step) };
                slots[first + i] = mesh.MaterialSlot;
            }

            ReadOnlySpan<uint> source = mesh.Indices.Span;
            List<uint> triangles = new(source.Length);
            for (int t = 0; t + 2 < source.Length; t += 3)
            {
                uint a = (uint)first + source[t];
                uint b = (uint)first + source[t + 1];
                uint c = (uint)first + source[t + 2];
                if (vertices[a].Position != vertices[b].Position && vertices[b].Position != vertices[c].Position
                    && vertices[a].Position != vertices[c].Position)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }
            }

            if (triangles.Count > 0) meshes.Add([.. triangles]);

            first += own.Length;
        }

        return (vertices, slots, meshes);
    }

    /// <summary>
    /// <paramref name="position"/> on the grid <paramref name="step"/> metres apart from the model's origin. A zero is
    /// always +0, so two vertices at one place have the same bits, which is how meshoptimizer matches places.
    /// </summary>
    private static Vector3 Snap(Vector3 position, float step)
    {
        return new Vector3(SnapAxis(position.X), SnapAxis(position.Y), SnapAxis(position.Z));

        float SnapAxis(float value)
        {
            float snapped = (float)(Math.Round(value / (double)step) * step);
            return snapped == 0 ? 0 : snapped;
        }
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

        if (!any) return 0;

        return farthest > 0 ? farthest / PositionSteps : 1e-6f;
    }

    /// <summary>
    /// For each vertex, the first vertex at its place, so what meets only by place (two material slots, two chunks, a
    /// uv seam) is grouped and locked as what shares a vertex is.
    /// </summary>
    private static unsafe uint[] Weld(Vertex[] vertices)
    {
        uint[] places = new uint[vertices.Length];
        fixed (uint* destination = places)
        fixed (Vertex* first = vertices)
        {
            Meshopt.GeneratePositionRemap(destination, (float*)first, (ulong)vertices.Length, Vertex.Size);
        }

        return places;
    }

    /// <summary>
    /// The finest clusters: each mesh split on its own, one larger than <see cref="ChunkTriangles"/> in chunks along a
    /// spatial sort, every chunk at once, the clusters in the meshes' order.
    /// </summary>
    private static unsafe List<Part> SplitFinest(
        Vertex[] vertices,
        int[] slots,
        List<uint[]> meshes,
        CancellationToken cancel)
    {
        List<uint[]> chunks = [];
        foreach (uint[] mesh in meshes)
        {
            if (mesh.Length <= ChunkTriangles * 3)
            {
                chunks.Add(mesh);
                continue;
            }

            uint[] sorted = new uint[mesh.Length];
            fixed (uint* destination = sorted)
            fixed (uint* indices = mesh)
            fixed (Vertex* first = vertices)
            {
                Meshopt.SpatialSortTriangles(
                    destination,
                    indices,
                    (ulong)mesh.Length,
                    (float*)first,
                    (ulong)vertices.Length,
                    Vertex.Size);
            }

            for (int start = 0; start < sorted.Length; start += ChunkTriangles * 3)
            {
                chunks.Add(sorted[start..Math.Min(start + (ChunkTriangles * 3), sorted.Length)]);
            }
        }

        List<Part>[] made = new List<Part>[chunks.Count];
        Parallel.For(0, chunks.Count, new ParallelOptions { CancellationToken = cancel }, c =>
        {
            (Vertex[] own, uint[] triangles, uint[] sources) = Localize(chunks[c], vertices);
            made[c] = Split(own, triangles, sources, slots[chunks[c][0]], 0, 0, null);
        });
        List<Part> parts = [];
        foreach (List<Part> chunk in made)
        {
            parts.AddRange(chunk);
        }

        return parts;
    }

    /// <summary>
    /// <paramref name="triangles"/>, into <paramref name="vertices"/>, as triangles into a copy of the vertices they
    /// use alone, with the vertex each copy is: what meshoptimizer works on, so its work is the size of the piece and
    /// not of the model.
    /// </summary>
    private static (Vertex[] Own, uint[] Triangles, uint[] Sources) Localize(
        ReadOnlySpan<uint> triangles,
        Vertex[] vertices)
    {
        Dictionary<uint, uint> local = [];
        List<uint> sources = [];
        uint[] localTriangles = new uint[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            if (!local.TryGetValue(triangles[i], out uint index))
            {
                index = (uint)sources.Count;
                local[triangles[i]] = index;
                sources.Add(triangles[i]);
            }

            localTriangles[i] = index;
        }

        Vertex[] own = new Vertex[sources.Count];
        for (int i = 0; i < own.Length; i++)
        {
            own[i] = vertices[sources[i]];
        }

        return (own, localTriangles, [.. sources]);
    }

    /// <summary>
    /// <paramref name="triangles"/> of one material slot, into <paramref name="own"/>, split by meshoptimizer into
    /// compact clusters of at most <see cref="ClusterHierarchy.MaxTriangles"/> triangles and
    /// <see cref="ClusterHierarchy.MaxVertices"/> vertices, each holding the model's vertices <paramref name="sources"/>
    /// says its own are.
    /// </summary>
    private static unsafe List<Part> Split(
        Vertex[] own,
        uint[] triangles,
        uint[] sources,
        int slot,
        int level,
        float error,
        Vector4? errorBounds)
    {
        ulong bound = Meshopt.BuildMeshletsBound((ulong)triangles.Length, ClusterHierarchy.MaxVertices, MinTriangles);
        Meshopt.Meshlet[] meshlets = new Meshopt.Meshlet[bound];
        uint[] meshletVertices = new uint[triangles.Length];
        byte[] meshletTriangles = new byte[triangles.Length];
        int count;
        fixed (Meshopt.Meshlet* made = meshlets)
        fixed (uint* madeVertices = meshletVertices)
        fixed (byte* madeTriangles = meshletTriangles)
        fixed (uint* indices = triangles)
        fixed (Vertex* first = own)
        {
            count = (int)Meshopt.BuildMeshletsFlex(
                made,
                madeVertices,
                madeTriangles,
                indices,
                (ulong)triangles.Length,
                (float*)first,
                (ulong)own.Length,
                Vertex.Size,
                ClusterHierarchy.MaxVertices,
                MinTriangles,
                ClusterHierarchy.MaxTriangles,
                0,
                SplitFactor);
        }

        List<Part> parts = new(count);
        for (int i = 0; i < count; i++)
        {
            Meshopt.Meshlet meshlet = meshlets[i];
            fixed (uint* vertices = &meshletVertices[meshlet.VertexOffset])
            fixed (byte* corners = &meshletTriangles[meshlet.TriangleOffset])
            {
                Meshopt.OptimizeMeshlet(vertices, corners, meshlet.TriangleCount, meshlet.VertexCount);
            }

            uint[] held = new uint[meshlet.VertexCount];
            Vertex[] placed = new Vertex[meshlet.VertexCount];
            for (int v = 0; v < held.Length; v++)
            {
                uint local = meshletVertices[meshlet.VertexOffset + v];
                held[v] = sources[local];
                placed[v] = own[local];
            }

            Vector4 bounds = ComputeSphere(placed);
            Vector4 errorSphere = errorBounds ?? bounds;
            int offset = (int)meshlet.TriangleOffset;
            parts.Add(new Part
            {
                Vertices = held,
                Indices = meshletTriangles[offset..(offset + ((int)meshlet.TriangleCount * 3))],
                Slot = slot,
                Level = level,
                Bounds = bounds,
                ErrorBounds = errorSphere,
                ParentErrorBounds = errorSphere,
                Error = error,
            });
        }

        return parts;
    }

    /// <summary>
    /// Clusters grouped with their neighbours by meshoptimizer: by the places they share, so a group's border with the
    /// rest is short, and by where they are, so clusters that are near but do not meet go together.
    /// </summary>
    private static unsafe List<List<Part>> Group(List<Part> parts, Vertex[] vertices, uint[] places)
    {
        uint[] counts = new uint[parts.Count];
        int total = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            counts[p] = (uint)parts[p].Indices.Length;
            total += parts[p].Indices.Length;
        }

        uint[] corners = new uint[total];
        int at = 0;
        foreach (Part part in parts)
        {
            foreach (byte index in part.Indices)
            {
                corners[at++] = places[part.Vertices[index]];
            }
        }

        uint[] partition = new uint[parts.Count];
        int count;
        fixed (uint* destination = partition)
        fixed (uint* clusterIndices = corners)
        fixed (uint* clusterCounts = counts)
        fixed (Vertex* first = vertices)
        {
            count = (int)Meshopt.PartitionClusters(
                destination,
                clusterIndices,
                (ulong)total,
                clusterCounts,
                (ulong)parts.Count,
                (float*)first,
                (ulong)vertices.Length,
                Vertex.Size,
                GroupSize);
        }

        List<List<Part>> groups = new(count);
        for (int g = 0; g < count; g++)
        {
            groups.Add([]);
        }

        for (int p = 0; p < parts.Count; p++)
        {
            groups[(int)partition[p]].Add(parts[p]);
        }

        return groups;
    }

    /// <summary>
    /// For each vertex, whether the simplifier must leave it where it is: at a place two groups share, or at one
    /// frozen at an earlier level.
    /// </summary>
    private static byte[] LockBorders(List<List<Part>> groups, uint[] places, bool[] frozen)
    {
        const int None = -2;
        const int Shared = -1;
        int[] groupAt = new int[places.Length];
        Array.Fill(groupAt, None);
        for (int g = 0; g < groups.Count; g++)
        {
            foreach (Part part in groups[g])
            {
                foreach (uint vertex in part.Vertices)
                {
                    uint place = places[vertex];
                    groupAt[place] = groupAt[place] == None || groupAt[place] == g ? g : Shared;
                }
            }
        }

        byte[] locks = new byte[places.Length];
        foreach (List<Part> group in groups)
        {
            foreach (Part part in group)
            {
                foreach (uint vertex in part.Vertices)
                {
                    uint place = places[vertex];
                    if (groupAt[place] == Shared || frozen[place]) locks[vertex] = SimplifyVertex.Lock;
                }
            }
        }

        return locks;
    }

    /// <summary>
    /// A group that stopped simplifying has its border frozen: every place of it it was locked at.
    /// </summary>
    private static void Freeze(List<Part> group, uint[] places, byte[] locks, bool[] frozen)
    {
        foreach (Part part in group)
        {
            foreach (uint vertex in part.Vertices)
            {
                if (locks[vertex] != 0) frozen[places[vertex]] = true;
            }
        }
    }

    /// <summary>
    /// A group simplified by meshoptimizer to half its triangles with its locked vertices kept, and split into the
    /// clusters of the next level, each material slot's triangles on their own; its clusters take what they were
    /// simplified into as their parent. Null when it would keep too much (<see cref="MinReduction"/>), or nothing: its
    /// clusters stay the coarsest there are of their part of the model.
    /// </summary>
    private static unsafe List<Part>? Simplify(
        List<Part> group,
        Vertex[] vertices,
        int[] slots,
        byte[] locks,
        int level)
    {
        List<uint> corners = [];
        float error = 0;
        Vector4 bounds = group[0].ErrorBounds;
        foreach (Part part in group)
        {
            foreach (byte index in part.Indices)
            {
                corners.Add(part.Vertices[index]);
            }

            error = MathF.Max(error, part.Error);
            bounds = Enclose(bounds, part.ErrorBounds);
        }

        (Vertex[] own, uint[] triangles, uint[] sources) = Localize(CollectionsMarshal.AsSpan(corners), vertices);
        byte[] ownLocks = new byte[own.Length];
        for (int i = 0; i < own.Length; i++)
        {
            ownLocks[i] = locks[sources[i]];
        }

        int triangleCount = triangles.Length / 3;
        uint[] kept = new uint[triangles.Length];
        int keptCount;
        float moved;
        fixed (uint* destination = kept)
        fixed (uint* indices = triangles)
        fixed (Vertex* first = own)
        fixed (byte* locked = ownLocks)
        {
            keptCount = (int)Meshopt.SimplifyWithAttributes(
                destination,
                indices,
                (ulong)triangles.Length,
                (float*)first,
                (ulong)own.Length,
                Vertex.Size,
                null,
                0,
                null,
                0,
                locked,
                (ulong)(triangleCount / 2 * 3),
                float.MaxValue,
                SimplifyOptions.ErrorAbsolute,
                &moved);
        }

        if (keptCount == 0 || keptCount / 3 > triangleCount * MinReduction) return null;

        error = MathF.Max(error, moved);
        foreach (Part part in group)
        {
            part.ParentError = error;
            part.ParentErrorBounds = bounds;
        }

        // A kept triangle's slot is its corners': slots never share a vertex, and the simplifier only moves a corner
        // to another of the triangles around it.
        List<(int Slot, List<uint> Triangles)> bySlot = [];
        for (int t = 0; t < keptCount; t += 3)
        {
            int slot = slots[sources[kept[t]]];
            int found = bySlot.FindIndex(entry => entry.Slot == slot);
            if (found < 0)
            {
                found = bySlot.Count;
                bySlot.Add((slot, []));
            }

            bySlot[found].Triangles.AddRange(kept.AsSpan(t, 3));
        }

        List<Part> made = [];
        foreach ((int slot, List<uint> slotTriangles) in bySlot)
        {
            made.AddRange(Split(own, [.. slotTriangles], sources, slot, level, error, bounds));
        }

        return made;
    }

    /// <summary>
    /// The parts as Core's hierarchy, coarsest level last, each cluster's vertices a run of its own; those never
    /// simplified into anything have no parent.
    /// </summary>
    private static ClusterHierarchy BuildHierarchy(List<Part> parts, Vertex[] vertices, float step)
    {
        parts.Sort(static (left, right) => left.Level.CompareTo(right.Level));
        int vertexCount = 0;
        int indexCount = 0;
        foreach (Part part in parts)
        {
            vertexCount += part.Vertices.Length;
            indexCount += part.Indices.Length;
        }

        Vertex[] held = new Vertex[vertexCount];
        byte[] indices = new byte[indexCount];
        Cluster[] clusters = new Cluster[parts.Count];
        int firstVertex = 0;
        int firstIndex = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            Part part = parts[i];
            clusters[i] = new Cluster
            {
                FirstVertex = firstVertex,
                VertexCount = part.Vertices.Length,
                FirstIndex = firstIndex,
                TriangleCount = part.Indices.Length / 3,
                MaterialSlot = part.Slot,
                Level = part.Level,
                Bounds = part.Bounds,
                ErrorBounds = part.ErrorBounds,
                Error = part.Error,
                ParentErrorBounds = part.ParentErrorBounds,
                ParentError = part.ParentError,
            };
            for (int v = 0; v < part.Vertices.Length; v++)
            {
                held[firstVertex + v] = vertices[part.Vertices[v]];
            }

            part.Indices.CopyTo(indices, firstIndex);
            firstVertex += part.Vertices.Length;
            firstIndex += part.Indices.Length;
        }

        return new ClusterHierarchy
        {
            Vertices = held,
            Indices = indices,
            Clusters = clusters,
            PositionStep = step
        };
    }

    /// <summary>
    /// The smallest sphere around both spheres.
    /// </summary>
    private static Vector4 Enclose(Vector4 first, Vector4 second)
    {
        Vector3 a = new(first.X, first.Y, first.Z);
        Vector3 b = new(second.X, second.Y, second.Z);
        float distance = Vector3.Distance(a, b);
        if (distance + second.W <= first.W) return first;

        if (distance + first.W <= second.W) return second;

        float radius = (distance + first.W + second.W) / 2;
        Vector3 centre = a + ((b - a) * ((radius - first.W) / distance));
        return new Vector4(centre, radius);
    }

    /// <summary>
    /// A cluster being made: the model's vertices it holds, its triangles into them, and what picking it takes.
    /// </summary>
    private sealed class Part
    {
        /// <summary>
        /// The model's vertices it holds, by where they are in the one array every level indexes.
        /// </summary>
        internal required uint[] Vertices { get; init; }

        /// <summary>
        /// Three a triangle, into <see cref="Vertices"/>.
        /// </summary>
        internal required byte[] Indices { get; init; }

        internal required int Slot { get; init; }

        internal required int Level { get; init; }

        internal required Vector4 Bounds { get; init; }

        internal required Vector4 ErrorBounds { get; init; }

        internal required float Error { get; init; }

        /// <summary>
        /// The error bounds of what it was simplified into; its own until it is.
        /// </summary>
        internal required Vector4 ParentErrorBounds { get; set; }

        internal float ParentError { get; set; } = float.PositiveInfinity;
    }
}
