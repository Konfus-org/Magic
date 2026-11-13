using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.World;
using static Magic.ClusteredGeometry.meshoptimizer;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Builds a model's cluster hierarchy, the DAG of Karis, "Nanite: A Deep Dive", 2021.
/// </summary>
/// <remarks>
/// The leaves are clusters of the model's triangles. Each level above groups neighbouring clusters, simplifies the
/// group's triangles to about half with the group's border locked, and clusters the result, until one group is left or
/// nothing simplifies further. The last level is the root: the whole mesh simplified, without regard to its topology,
/// down to a few triangles. Every cluster records its own error and its parent group's, each no less than its
/// children's, so the GPU decides the cut through the hierarchy per cluster. A root's parent error is the model's
/// diameter, so a model stops drawing when the whole of it is under the threshold. It has only pure functions over
/// arrays.
/// </remarks>
internal static unsafe class ClusterBuilder
{
    /// <summary>
    /// The most triangles a mesh's root holds: what a model draws as when it is a few pixels across.
    /// </summary>
    internal const int RootTriangles = 12;

    private const int GroupSize = 8;

    private const int MaxLevels = 24;

    /// <summary>
    /// How much a group must shrink for its simplification to count as a level; below it the group's clusters are
    /// roots.
    /// </summary>
    private const float MinReduction = 0.8f;

    /// <summary>
    /// The weight of a cluster's normal cone against its spatial tightness as the leaves are clustered.
    /// </summary>
    private const float ConeWeight = 0.25f;

    /// <summary>
    /// Faces meeting at a vertex at more than this angle, in radians, keep their own normals in a simplified level.
    /// </summary>
    private const float CreaseAngle = MathF.PI / 4;

    public static ModelClusters Build(Model model)
    {
        // The model's own vertices first, every mesh's, which the leaves draw, then each level's copies.
        ImmutableArray<GpuVertex>.Builder vertices = ImmutableArray.CreateBuilder<GpuVertex>();
        List<uint> firstVertices = [];
        foreach (Mesh mesh in model.Meshes)
        {
            firstVertices.Add((uint)vertices.Count);
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                vertices.Add(Packing.EncodeVertex(vertex));
            }
        }

        int modelVertexCount = vertices.Count;
        List<uint> indices = [];
        List<GpuCluster> clusters = [];
        for (int i = 0; i < model.Meshes.Length; i++)
        {
            BuildMesh(model.Meshes[i], firstVertices[i], vertices, indices, clusters);
        }

        // The box around the model, which its impostor's quad stays inside, and the sphere around it: its centre the
        // box's middle, 0 for a model with no vertices, its radius the farthest vertex from there.
        Vector3 boundsMin = new(float.MaxValue);
        Vector3 boundsMax = new(float.MinValue);
        foreach (GpuVertex vertex in vertices)
        {
            boundsMin = Vector3.Min(boundsMin, vertex.Position);
            boundsMax = Vector3.Max(boundsMax, vertex.Position);
        }

        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        float radius2 = 0;
        foreach (GpuVertex vertex in vertices)
        {
            radius2 = Math.Max(radius2, Vector3.DistanceSquared(center, vertex.Position));
        }

        // Every root's parent: the model's sphere, widened to hold the roots' own, at the error of its diameter, so a
        // root draws until the whole model projects under the threshold, and then nothing does.
        float radius = MathF.Sqrt(radius2);
        float rootsRadius = radius;
        foreach (GpuCluster cluster in clusters)
        {
            if (float.IsPositiveInfinity(cluster.ParentError))
                rootsRadius = Math.Max(
                    rootsRadius,
                    Vector3.Distance(center, cluster.DetailCenter) + cluster.DetailRadius
                );
        }

        for (int i = 0; i < clusters.Count; i++)
        {
            GpuCluster cluster = clusters[i];
            if (!float.IsPositiveInfinity(cluster.ParentError))
                continue;

            cluster.ParentError = 2 * rootsRadius;
            cluster.ParentCenter = center;
            cluster.ParentRadius = rootsRadius;
            clusters[i] = cluster;
        }

        // Every model stands far away as an impostor once it is a few pixels across.
        (Half[] Surface, Half[] Uv, uint[] ViewBounds) impostor = ImpostorBuilder.Bake(
            vertices,
            indices,
            clusters,
            center,
            radius
        );

        return new ModelClusters
        {
            ModelVertexCount = modelVertexCount,
            Vertices = [.. vertices.Skip(modelVertexCount)],
            Indices = [.. indices],
            Clusters = [.. clusters],
            Impostor = [.. impostor.Surface],
            ImpostorUv = [.. impostor.Uv],
            ImpostorViewBounds = [.. impostor.ViewBounds],
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
        };
    }

    /// <summary>
    /// Builds one mesh's hierarchy, and appends its clusters, leaves first, and its indices, as each cluster's run.
    /// </summary>
    /// <remarks>
    /// The leaves draw the mesh's own vertices. Each level above draws copies of the vertices that its simplification
    /// kept, with normals made from its own triangles, because the simplifier joins the vertices split at a hard edge
    /// or a seam.
    /// </remarks>
    private static void BuildMesh(
        Mesh mesh,
        uint firstVertex,
        ImmutableArray<GpuVertex>.Builder vertices,
        List<uint> indices,
        List<GpuCluster> clusters
    )
    {
        ReadOnlySpan<Vertex> source = mesh.Vertices.Span;
        Vector3[] positions = new Vector3[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            positions[i] = source[i].Position;
        }

        float scale;
        fixed (Vector3* p = positions)
        {
            scale = meshopt_simplifyScale((float*)p, (nuint)positions.Length, (nuint)sizeof(Vector3));
        }

        // For each vertex, the first vertex at the same position: what the simplifier joins the mesh by, so vertices
        // split for a hard edge or a texture seam still make one surface.
        ReadOnlySpan<uint> meshIndices = mesh.Indices.Span;
        uint[] shadow = new uint[meshIndices.Length];
        fixed (uint* d = shadow)
        fixed (uint* i = meshIndices)
        fixed (Vector3* p = positions)
        {
            meshopt_generateShadowIndexBuffer(
                d,
                i,
                (nuint)meshIndices.Length,
                p,
                (nuint)positions.Length,
                (nuint)sizeof(Vector3),
                (nuint)sizeof(Vector3)
            );
        }

        uint[] canonical = new uint[positions.Length];
        for (int v = 0; v < canonical.Length; v++)
        {
            canonical[v] = (uint)v;
        }

        for (int i = 0; i < meshIndices.Length; i++)
        {
            canonical[meshIndices[i]] = shadow[i];
        }

        // Each level is a list of clusters, each a list of mesh-local indices; the leaves are the mesh itself.
        List<Cluster> level = Clusterize(meshIndices.ToArray(), positions, error: 0, materialSlot: mesh.MaterialSlot);
        foreach (Cluster leaf in level)
        {
            leaf.PoolIndices = new uint[leaf.Indices.Length];
            for (int i = 0; i < leaf.Indices.Length; i++)
            {
                leaf.PoolIndices[i] = firstVertex + leaf.Indices[i];
            }
        }

        Emit(level, indices, clusters);
        for (int depth = 0; depth < MaxLevels && level.Count > 1; depth++)
        {
            // Which group each cluster of the level is in, neighbours together.
            uint[] all = [.. level.SelectMany(cluster => cluster.Indices)];
            uint[] counts = [.. level.Select(cluster => (uint)cluster.Indices.Length)];
            uint[] groups = new uint[level.Count];
            fixed (uint* d = groups)
            fixed (uint* a = all)
            fixed (uint* c = counts)
            fixed (Vector3* p = positions)
            {
                meshopt_partitionClusters(
                    d,
                    a,
                    (nuint)all.Length,
                    c,
                    (nuint)level.Count,
                    (float*)p,
                    (nuint)positions.Length,
                    (nuint)sizeof(Vector3),
                    GroupSize
                );
            }

            // Every group simplified first, so the level's vertex copies take their normals from the whole level: a
            // vertex on the border of two groups then shades the same on both sides. A group that won't simplify
            // leaves its children roots.
            uint groupCount = groups.Max() + 1;
            Span<GpuCluster> emitted = CollectionsMarshal.AsSpan(clusters)[^level.Count..];
            List<Group> made = [];
            for (uint group = 0; group < groupCount; group++)
            {
                int[] members = [.. Enumerable.Range(0, level.Count).Where(i => groups[i] == group)];
                if (members.Length < 2)
                    continue;

                uint[] merged = [.. members.SelectMany(i => level[i].Indices)];
                (uint[] simplified, float error) = Simplify(
                    merged,
                    positions,
                    canonical,
                    scale,
                    merged.Length / 2 / 3 * 3,
                    isSloppy: false
                );
                if (simplified.Length > merged.Length * MinReduction)
                    continue;

                float childError = members.Max(i => level[i].Gpu.Error);
                made.Add(SetParent(members, Math.Max(error, childError), simplified, emitted));
            }

            List<Cluster> parents = made.Count == 0 ? [] : AddLevel(made);
            Log.Verbose($"Level {depth}: {level.Count} clusters grouped into {parents.Count} above them.");
            if (parents.Count == 0)
                break;

            Emit(parents, indices, clusters);
            level = parents;
        }

        // The root above the last level: the level's triangles simplified without regard to their topology down to
        // RootTriangles, clustered, with the level's clusters as its children; none when the level is already that
        // small.
        uint[] levelIndices = [.. level.SelectMany(cluster => cluster.Indices)];
        if (levelIndices.Length <= RootTriangles * 3)
            return;

        (uint[] rootIndices, float rootError) = Simplify(
            levelIndices,
            positions,
            canonical,
            scale,
            RootTriangles * 3,
            isSloppy: true
        );
        if (rootIndices.Length == 0 || rootIndices.Length >= levelIndices.Length)
            return;

        float levelError = level.Max(cluster => cluster.Gpu.Error);
        Span<GpuCluster> roots = CollectionsMarshal.AsSpan(clusters)[^level.Count..];
        Group root = SetParent(
            [.. Enumerable.Range(0, level.Count)],
            Math.Max(rootError, levelError),
            rootIndices,
            roots
        );
        Emit(AddLevel([root]), indices, clusters);

        // Writes a group's parent error and sphere, the sphere around its clusters' detail spheres, into its members,
        // and makes the group. A parent's error is never below its children's, and its sphere holds theirs, so the cut
        // never jumps back to finer detail as the camera moves.
        static Group SetParent(int[] members, float error, uint[] simplified, Span<GpuCluster> emitted)
        {
            Vector3[] centers = new Vector3[members.Length];
            float[] radii = new float[members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                centers[i] = emitted[members[i]].DetailCenter;
                radii[i] = emitted[members[i]].DetailRadius;
            }

            meshopt_Bounds bounds;
            fixed (Vector3* c = centers)
            fixed (float* r = radii)
            {
                bounds = meshopt_computeSphereBounds(
                    (float*)c,
                    (nuint)members.Length,
                    (nuint)sizeof(Vector3),
                    r,
                    sizeof(float)
                );
            }

            Vector3 center = new(bounds.center[0], bounds.center[1], bounds.center[2]);
            foreach (int member in members)
            {
                ref GpuCluster child = ref emitted[member];
                child.ParentError = error;
                child.ParentCenter = center;
                child.ParentRadius = bounds.radius;
            }

            return new Group(simplified, error, center, bounds.radius);
        }

        // The level the groups make: each group's triangles clustered, every cluster with the group's error and
        // sphere, and the level's vertices copied into the pool, once for the whole level, every cluster's triangles
        // in the pool.
        List<Cluster> AddLevel(List<Group> made)
        {
            List<Cluster> parents = [];
            foreach (Group group in made)
            {
                foreach (Cluster parent in Clusterize(group.Simplified, positions, group.Error, mesh.MaterialSlot))
                {
                    parent.Gpu.DetailCenter = group.Center;
                    parent.Gpu.DetailRadius = group.Radius;
                    parents.Add(parent);
                }
            }

            uint[] pool = CopyVertices(
                mesh,
                [.. made.SelectMany(group => group.Simplified)],
                [.. parents.SelectMany(parent => parent.Indices)],
                positions,
                canonical,
                vertices
            );
            int first = 0;
            foreach (Cluster parent in parents)
            {
                parent.PoolIndices = pool[first..(first + parent.Indices.Length)];
                first += parent.Indices.Length;
            }

            return parents;
        }
    }

    /// <summary>
    /// Simplifies the triangles to at most a target count.
    /// </summary>
    /// <remarks>
    /// The ordinary simplifier keeps the border locked so neighbouring groups keep meeting. The sloppy one has no
    /// regard for the topology and reaches what the ordinary one cannot.
    /// </remarks>
    /// <returns>The simplified triangles, and the error that cost in the mesh's units.</returns>
    private static (uint[] Indices, float Error) Simplify(
        uint[] indices,
        Vector3[] positions,
        uint[] canonical,
        float scale,
        int targetIndices,
        bool isSloppy
    )
    {
        // Simplified over the canonical vertices, so a seam in the attributes is not a border the simplifier keeps.
        uint[] welded = new uint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            welded[i] = canonical[indices[i]];
        }

        uint[] destination = new uint[indices.Length];
        float error;
        nuint count;
        fixed (uint* d = destination)
        fixed (uint* i = welded)
        fixed (Vector3* p = positions)
        {
            count = isSloppy
                ? meshopt_simplifySloppy(
                    d,
                    i,
                    (nuint)indices.Length,
                    (float*)p,
                    (nuint)positions.Length,
                    (nuint)sizeof(Vector3),
                    null,
                    (nuint)targetIndices,
                    float.MaxValue,
                    &error
                )
                : meshopt_simplify(
                    d,
                    i,
                    (nuint)indices.Length,
                    (float*)p,
                    (nuint)positions.Length,
                    (nuint)sizeof(Vector3),
                    (nuint)targetIndices,
                    float.MaxValue,
                    meshopt_SimplifyLockBorder | meshopt_SimplifySparse,
                    &error
                );
        }

        return (destination.AsSpan(0, (int)count).ToArray(), error * scale);
    }

    /// <summary>
    /// Cuts <paramref name="indices"/> into clusters of at most <see cref="GpuCluster.MaxTriangles"/> triangles,
    /// each with its bounds and cone, at <paramref name="error"/>.
    /// </summary>
    private static List<Cluster> Clusterize(uint[] indices, Vector3[] positions, float error, int materialSlot)
    {
        nuint bound = meshopt_buildMeshletsBound(
            (nuint)indices.Length,
            GpuCluster.MaxTriangles,
            GpuCluster.MaxTriangles
        );
        meshopt_Meshlet[] meshlets = new meshopt_Meshlet[bound];
        uint[] meshletVertices = new uint[bound * GpuCluster.MaxTriangles];
        byte[] meshletTriangles = new byte[bound * GpuCluster.MaxTriangles * 3];
        nuint count;
        fixed (meshopt_Meshlet* m = meshlets)
        fixed (uint* v = meshletVertices)
        fixed (byte* t = meshletTriangles)
        fixed (uint* i = indices)
        fixed (Vector3* p = positions)
        {
            count = meshopt_buildMeshlets(
                m,
                v,
                t,
                i,
                (nuint)indices.Length,
                (float*)p,
                (nuint)positions.Length,
                (nuint)sizeof(Vector3),
                GpuCluster.MaxTriangles,
                GpuCluster.MaxTriangles,
                ConeWeight
            );
        }

        List<Cluster> level = [];
        for (int c = 0; c < (int)count; c++)
        {
            meshopt_Meshlet meshlet = meshlets[c];
            uint[] local = new uint[meshlet.triangle_count * 3];
            for (uint k = 0; k < meshlet.triangle_count * 3; k++)
            {
                local[k] = meshletVertices[meshlet.vertex_offset + meshletTriangles[meshlet.triangle_offset + k]];
            }

            meshopt_Bounds bounds;
            fixed (uint* v = meshletVertices)
            fixed (byte* t = meshletTriangles)
            fixed (Vector3* p = positions)
            {
                bounds = meshopt_computeMeshletBounds(
                    v + meshlet.vertex_offset,
                    t + meshlet.triangle_offset,
                    meshlet.triangle_count,
                    (float*)p,
                    (nuint)positions.Length,
                    (nuint)sizeof(Vector3)
                );
            }

            Vector3 center = new(bounds.center[0], bounds.center[1], bounds.center[2]);
            Vector3 coneAxis = new(bounds.cone_axis[0], bounds.cone_axis[1], bounds.cone_axis[2]);
            level.Add(
                new Cluster
                {
                    Indices = local,
                    Gpu = new GpuCluster
                    {
                        Center = center,
                        Radius = bounds.radius,
                        DetailCenter = center,
                        DetailRadius = bounds.radius,
                        Error = error,
                        TriangleCount = meshlet.triangle_count,
                        ConeAxis = Packing.EncodeUnitVector(coneAxis),
                        ConeCutoff = Packing.EncodeSnorm16(bounds.cone_cutoff),
                        MaterialSlot = (uint)materialSlot,
                    },
                }
            );
        }

        return level;
    }

    /// <summary>
    /// Appends copies of the vertices that the simplified triangles use to the pool, with normals from those triangles
    /// alone.
    /// </summary>
    /// <remarks>
    /// A vertex has one copy per smoothing group of its faces, those within <see cref="CreaseAngle"/> of each other, so
    /// a hard edge stays hard. The faces are gathered by position, so the vertices split along a seam shade alike.
    /// </remarks>
    /// <returns>
    /// The pool index of each corner's copy, by each corner of <paramref name="clustered"/>, the same triangles
    /// clustered.
    /// </returns>
    private static uint[] CopyVertices(
        Mesh mesh,
        uint[] simplified,
        uint[] clustered,
        Vector3[] positions,
        uint[] canonical,
        ImmutableArray<GpuVertex>.Builder vertices
    )
    {
        // The smoothing groups in the simplifier's order, as a group's first face sets the normal it is joined by.
        float creaseCosine = MathF.Cos(CreaseAngle);
        Dictionary<uint, List<SmoothingGroup>> groupsOf = [];
        for (int first = 0; first < simplified.Length; first += 3)
        {
            (Vector3 face, Vector3 unit) = GetFace(simplified, first);
            for (int corner = first; corner < first + 3; corner++)
            {
                uint vertex = canonical[simplified[corner]];
                if (!groupsOf.TryGetValue(vertex, out List<SmoothingGroup>? groups))
                {
                    groups = [];
                    groupsOf[vertex] = groups;
                }

                int found = groups.FindIndex(group => Vector3.Dot(group.Unit, unit) >= creaseCosine);
                if (found < 0)
                {
                    found = groups.Count;
                    groups.Add(new SmoothingGroup { Unit = unit });
                }

                groups[found].Sum += face;
            }
        }

        // Each clustered corner's copy: clustering keeps a triangle's corners in order, and a group's first normal
        // never changes, so the first group within the crease is the one the corner joined above.
        ReadOnlySpan<Vertex> source = mesh.Vertices.Span;
        Dictionary<(uint, int), uint> poolOf = [];
        uint[] pool = new uint[clustered.Length];
        for (int first = 0; first < clustered.Length; first += 3)
        {
            Vector3 unit = GetFace(clustered, first).Unit;
            for (int corner = first; corner < first + 3; corner++)
            {
                List<SmoothingGroup> groups = groupsOf[canonical[clustered[corner]]];
                (uint vertex, int group) key = (
                    clustered[corner],
                    groups.FindIndex(group => Vector3.Dot(group.Unit, unit) >= creaseCosine)
                );
                if (poolOf.TryGetValue(key, out pool[corner]))
                    continue;

                Vertex vertex = source[(int)key.vertex];
                if (groups[key.group].Sum.LengthSquared() > 0)
                    vertex.Normal = Vector3.Normalize(groups[key.group].Sum);

                pool[corner] = (uint)vertices.Count;
                vertices.Add(Packing.EncodeVertex(vertex));
                poolOf[key] = pool[corner];
            }
        }

        return pool;

        // The triangle at corner at: clockwise from outside, so the cross of the edges points out, its length
        // weighing the face by its area.
        (Vector3 Face, Vector3 Unit) GetFace(uint[] triangles, int at)
        {
            Vector3 p0 = positions[triangles[at]];
            Vector3 face = Vector3.Cross(positions[triangles[at + 1]] - p0, positions[triangles[at + 2]] - p0);
            return (face, face.LengthSquared() > 0 ? Vector3.Normalize(face) : Vector3.UnitY);
        }
    }

    /// <summary>
    /// Appends a level's clusters to the tables, roots until a group claims them.
    /// </summary>
    private static void Emit(List<Cluster> level, List<uint> indices, List<GpuCluster> clusters)
    {
        foreach (Cluster cluster in level)
        {
            clusters.Add(cluster.Gpu with { IndexOffset = (uint)indices.Count, ParentError = float.PositiveInfinity });
            indices.AddRange(cluster.PoolIndices);
        }
    }

    /// <summary>
    /// A group of a level's clusters simplified into their parent's triangles, with the error and the sphere every
    /// cluster made of them carries.
    /// </summary>
    private sealed record Group(uint[] Simplified, float Error, Vector3 Center, float Radius);

    /// <summary>
    /// The faces around a vertex that share a normal: their unit normal when the group began, and the area-weighted
    /// sum of them all.
    /// </summary>
    private sealed class SmoothingGroup
    {
        public required Vector3 Unit { get; init; }

        public Vector3 Sum { get; set; }
    }

    /// <summary>
    /// One cluster while its level is built.
    /// </summary>
    /// <remarks>
    /// It holds its triangles as mesh-local indices, the pool indices it draws with, and its row as the GPU reads it,
    /// short of where its indices land and what its parent group measures.
    /// </remarks>
    private sealed class Cluster
    {
        public required uint[] Indices;

        /// <summary>
        /// The same triangles as indices into the pool: the mesh's vertices for a leaf, a level's copies above.
        /// </summary>
        public uint[] PoolIndices = [];

        public GpuCluster Gpu;
    }
}
