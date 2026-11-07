using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Rendering;
using static Magic.MeshPreprocessor.meshoptimizer;

namespace Magic.MeshPreprocessor;

/// <summary>
/// Builds a model's cluster hierarchy, the DAG of Karis, "Nanite: A Deep Dive", 2021: the leaves are clusters of
/// its triangles; each level above groups neighbouring clusters, simplifies the group's triangles to about half with
/// the group's border locked, and clusters the result, until one group is left or nothing simplifies further; the
/// last level is the root, the whole mesh simplified without regard to its topology down to a few triangles. Every
/// cluster records its own error and its parent group's, each no less than its children's, so the cut through the
/// hierarchy is decided per cluster on the GPU; a root's parent error is the model's diameter, so a model stops
/// drawing when the whole of it is under the threshold. Pure functions over arrays, which a test drives with a
/// known mesh.
/// </summary>
internal static unsafe class ClusterBuilder
{
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

    /// <summary>
    /// The most triangles a mesh's root holds: what a model draws as when it is a few pixels across.
    /// </summary>
    internal const int RootTriangles = 12;

    public static MeshDerived Build(Model model)
    {
        ImmutableArray<GpuVertex>.Builder vertices = ImmutableArray.CreateBuilder<GpuVertex>();
        List<uint> indices = [];
        List<GpuCluster> clusters = [];
        foreach (Mesh mesh in model.Meshes)
        {
            uint firstVertex = (uint)vertices.Count;
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                vertices.Add(ToGpu(vertex));
            }

            BuildMesh(mesh, firstVertex, vertices, indices, clusters);
        }

        (Vector3 center, float radius) = GetSphere(vertices);
        SetRootParents(clusters, center, radius);
        DistanceField field = DistanceFieldBuilder.Build(model);

        return new MeshDerived
        {
            Vertices = vertices.ToImmutable(),
            Indices = [.. indices],
            Clusters = [.. clusters],
            BoundsCenter = center,
            BoundsRadius = radius,
            Field = field.Voxels,
            FieldOrigin = field.Origin,
            FieldVoxel = field.Voxel,
            FieldBand = field.Band,
        };
    }

    /// <summary>
    /// Every root's parent: the model's sphere, widened to hold the roots' own, at the error of its diameter, so a
    /// root draws until the whole model projects under the threshold, and then nothing does.
    /// </summary>
    private static void SetRootParents(List<GpuCluster> clusters, Vector3 center, float radius)
    {
        foreach (GpuCluster cluster in clusters)
        {
            if (float.IsPositiveInfinity(cluster.ParentError))
                radius = Math.Max(radius, Vector3.Distance(center, cluster.DetailCenter) + cluster.DetailRadius);
        }

        for (int i = 0; i < clusters.Count; i++)
        {
            GpuCluster cluster = clusters[i];
            if (!float.IsPositiveInfinity(cluster.ParentError))
                continue;

            cluster.ParentError = 2 * radius;
            cluster.ParentCenter = center;
            cluster.ParentRadius = radius;
            clusters[i] = cluster;
        }
    }

    /// <summary>
    /// One mesh's hierarchy, its clusters appended leaves first, its indices appended as each cluster's run. The
    /// leaves draw the mesh's own vertices; each level above draws copies of the vertices its simplification kept,
    /// with normals made from its own triangles, as the simplifier joins the vertices split at a hard edge or a seam.
    /// </summary>
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

        uint[] canonical = GetCanonicalVertices(mesh, positions);

        // Each level is a list of clusters, each a list of mesh-local indices; the leaves are the mesh itself.
        List<Cluster> level = Clusterize(mesh.Indices.ToArray(), positions, error: 0, materialSlot: mesh.MaterialSlot);
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
            List<Cluster> parents = BuildParents(level, mesh, positions, canonical, scale, vertices, clusters);
            Log.Verbose($"Level {depth}: {level.Count} clusters grouped into {parents.Count} above them.");
            if (parents.Count == 0)
                break;

            Emit(parents, indices, clusters);
            level = parents;
        }

        List<Cluster> root = BuildRoot(level, mesh, positions, canonical, scale, vertices, clusters);
        if (root.Count > 0)
            Emit(root, indices, clusters);
    }

    /// <summary>
    /// The root above the last level: the level's triangles simplified without regard to their topology down to
    /// <see cref="RootTriangles"/>, clustered, with the level's clusters as its children; none when the level is
    /// already that small.
    /// </summary>
    private static List<Cluster> BuildRoot(
        List<Cluster> level,
        Mesh mesh,
        Vector3[] positions,
        uint[] canonical,
        float scale,
        ImmutableArray<GpuVertex>.Builder vertices,
        List<GpuCluster> clusters
    )
    {
        int[] members = [.. Enumerable.Range(0, level.Count)];
        uint[] merged = [.. level.SelectMany(cluster => cluster.Indices)];
        if (merged.Length <= RootTriangles * 3)
            return [];

        (uint[] simplified, float error) = Simplify(
            merged,
            positions,
            canonical,
            scale,
            RootTriangles * 3,
            isSloppy: true
        );
        if (simplified.Length == 0 || simplified.Length >= merged.Length)
            return [];

        float childError = level.Max(cluster => cluster.Gpu.Error);
        Span<GpuCluster> emitted = CollectionsMarshal.AsSpan(clusters)[^level.Count..];
        Group root = SetParent(members, Math.Max(error, childError), simplified, emitted);

        return AddLevel([root], mesh, positions, canonical, vertices);
    }

    /// <summary>
    /// Groups the level's clusters, simplifies each group and clusters the result: the level above. Each child's
    /// parent error and sphere are written into <paramref name="clusters"/> as its group is made; a group that
    /// won't simplify leaves its children roots.
    /// </summary>
    private static List<Cluster> BuildParents(
        List<Cluster> level,
        Mesh mesh,
        Vector3[] positions,
        uint[] canonical,
        float scale,
        ImmutableArray<GpuVertex>.Builder vertices,
        List<GpuCluster> clusters
    )
    {
        uint[] groups = Partition(level, positions);
        uint groupCount = groups.Max() + 1;
        Span<GpuCluster> emitted = CollectionsMarshal.AsSpan(clusters)[^level.Count..];

        // Every group simplified first, so the level's vertex copies take their normals from the whole level: a
        // vertex on the border of two groups then shades the same on both sides.
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

        return made.Count == 0 ? [] : AddLevel(made, mesh, positions, canonical, vertices);
    }

    /// <summary>
    /// Writes a group's parent error and sphere into its members, and makes the group. A parent's error is never
    /// below its children's, and its sphere holds theirs, so the cut never jumps back to finer detail as the camera
    /// moves.
    /// </summary>
    private static Group SetParent(int[] members, float error, uint[] simplified, Span<GpuCluster> emitted)
    {
        (Vector3 center, float radius) = GetGroupSphere(members, emitted);
        foreach (int member in members)
        {
            ref GpuCluster child = ref emitted[member];
            child.ParentError = error;
            child.ParentCenter = center;
            child.ParentRadius = radius;
        }

        return new Group(simplified, error, center, radius);
    }

    /// <summary>
    /// The level the groups make: their vertices copied into the pool, once for the whole level, and each group's
    /// triangles clustered, every cluster with the group's error and sphere and its triangles in the pool.
    /// </summary>
    private static List<Cluster> AddLevel(
        List<Group> made,
        Mesh mesh,
        Vector3[] positions,
        uint[] canonical,
        ImmutableArray<GpuVertex>.Builder vertices
    )
    {
        uint[] levelTriangles = [.. made.SelectMany(group => group.Simplified)];
        Dictionary<(uint, uint, uint), (uint, uint, uint)> copies = CopyVertices(
            mesh,
            levelTriangles,
            positions,
            canonical,
            vertices
        );
        List<Cluster> parents = [];
        foreach (Group group in made)
        {
            foreach (Cluster parent in Clusterize(group.Simplified, positions, group.Error, mesh.MaterialSlot))
            {
                parent.Gpu.DetailCenter = group.Center;
                parent.Gpu.DetailRadius = group.Radius;
                parent.PoolIndices = new uint[parent.Indices.Length];
                for (int i = 0; i + 2 < parent.Indices.Length; i += 3)
                {
                    (uint a, uint b, uint c) = copies[
                        (parent.Indices[i], parent.Indices[i + 1], parent.Indices[i + 2])
                    ];
                    parent.PoolIndices[i] = a;
                    parent.PoolIndices[i + 1] = b;
                    parent.PoolIndices[i + 2] = c;
                }

                parents.Add(parent);
            }
        }

        return parents;
    }

    /// <summary>
    /// Which group each cluster of the level is in, neighbours together.
    /// </summary>
    private static uint[] Partition(List<Cluster> level, Vector3[] positions)
    {
        uint[] all = [.. level.SelectMany(cluster => cluster.Indices)];
        uint[] counts = [.. level.Select(cluster => (uint)cluster.Indices.Length)];
        uint[] destination = new uint[level.Count];
        fixed (uint* d = destination)
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
                (nuint)GroupSize
            );
        }

        return destination;
    }

    /// <summary>
    /// The triangles simplified to at most a target count, and the error that cost in the mesh's units. The ordinary
    /// simplifier keeps the border locked so neighbouring groups keep meeting; the sloppy one has no regard for the
    /// topology and reaches what the ordinary one cannot.
    /// </summary>
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
                    (uint)(meshopt_SimplifyLockBorder | meshopt_SimplifySparse),
                    &error
                );
        }

        return (destination.AsSpan(0, (int)count).ToArray(), error * scale);
    }

    /// <summary>
    /// For each vertex, the first vertex at the same position: what the simplifier joins the mesh by, so vertices
    /// split for a hard edge or a texture seam still make one surface.
    /// </summary>
    private static uint[] GetCanonicalVertices(Mesh mesh, Vector3[] positions)
    {
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        uint[] shadow = new uint[indices.Length];
        fixed (uint* d = shadow)
        fixed (uint* i = indices)
        fixed (Vector3* p = positions)
        {
            meshopt_generateShadowIndexBuffer(
                d,
                i,
                (nuint)indices.Length,
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

        for (int i = 0; i < indices.Length; i++)
        {
            canonical[indices[i]] = shadow[i];
        }

        return canonical;
    }

    /// <summary>
    /// <paramref name="indices"/> cut into clusters of at most <see cref="GpuCluster.MaxTriangles"/> triangles,
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
    /// Copies of the vertices the simplified triangles use, appended to the pool, with normals from those triangles
    /// alone: a vertex has one copy per smoothing group of its faces, those within <see cref="CreaseAngle"/> of each
    /// other, so a hard edge stays hard; the faces are gathered by position, so the vertices split along a seam
    /// shade alike. By each triangle of the mesh's indices, the pool indices of its corners.
    /// </summary>
    private static Dictionary<(uint, uint, uint), (uint, uint, uint)> CopyVertices(
        Mesh mesh,
        uint[] simplified,
        Vector3[] positions,
        uint[] canonical,
        ImmutableArray<GpuVertex>.Builder vertices
    )
    {
        float creaseCosine = MathF.Cos(CreaseAngle);
        int triangleCount = simplified.Length / 3;
        Dictionary<uint, List<SmoothingGroup>> groupsOf = [];
        int[] groupOfCorner = new int[simplified.Length];
        for (int t = 0; t < triangleCount; t++)
        {
            Vector3 p0 = positions[simplified[t * 3]];
            Vector3 p1 = positions[simplified[(t * 3) + 1]];
            Vector3 p2 = positions[simplified[(t * 3) + 2]];

            // Clockwise from outside, so the cross of the edges points out; its length weighs the face by its area.
            Vector3 face = Vector3.Cross(p1 - p0, p2 - p0);
            Vector3 unit = face.LengthSquared() > 0 ? Vector3.Normalize(face) : Vector3.UnitY;
            for (int corner = 0; corner < 3; corner++)
            {
                uint vertex = canonical[simplified[(t * 3) + corner]];
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
                groupOfCorner[(t * 3) + corner] = found;
            }
        }

        ReadOnlySpan<Vertex> source = mesh.Vertices.Span;
        Dictionary<(uint, int), uint> poolOf = [];
        uint[] cornerPool = new uint[simplified.Length];
        for (int i = 0; i < simplified.Length; i++)
        {
            (uint vertex, int group) key = (simplified[i], groupOfCorner[i]);
            if (!poolOf.TryGetValue(key, out uint pool))
            {
                Vertex vertex = source[(int)key.vertex];
                Vector3 sum = groupsOf[canonical[key.vertex]][key.group].Sum;
                if (sum.LengthSquared() > 0)
                    vertex.Normal = Vector3.Normalize(sum);

                pool = (uint)vertices.Count;
                vertices.Add(ToGpu(vertex));
                poolOf[key] = pool;
            }

            cornerPool[i] = pool;
        }

        Dictionary<(uint, uint, uint), (uint, uint, uint)> copies = [];
        for (int t = 0; t < triangleCount; t++)
        {
            int at = t * 3;
            copies[(simplified[at], simplified[at + 1], simplified[at + 2])] = (
                cornerPool[at],
                cornerPool[at + 1],
                cornerPool[at + 2]
            );
        }

        return copies;
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
    /// The sphere around the group's clusters' detail spheres.
    /// </summary>
    private static (Vector3 Center, float Radius) GetGroupSphere(int[] members, ReadOnlySpan<GpuCluster> emitted)
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

        return (new Vector3(bounds.center[0], bounds.center[1], bounds.center[2]), bounds.radius);
    }

    /// <summary>
    /// The sphere around every vertex: its centre the bounds' middle, its radius the farthest vertex from there.
    /// </summary>
    private static (Vector3 Center, float Radius) GetSphere(ImmutableArray<GpuVertex>.Builder vertices)
    {
        if (vertices.Count == 0)
            return (Vector3.Zero, 0);

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (GpuVertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        Vector3 center = (min + max) * 0.5f;
        float radius2 = 0;
        foreach (GpuVertex vertex in vertices)
        {
            radius2 = Math.Max(radius2, Vector3.DistanceSquared(center, vertex.Position));
        }

        return (center, MathF.Sqrt(radius2));
    }

    private static GpuVertex ToGpu(in Vertex vertex) =>
        new()
        {
            Position = vertex.Position,
            Normal = Packing.EncodeUnitVector(vertex.Normal),
            Tangent = Packing.EncodeUnitVector(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z)),
            TangentSign = vertex.Tangent.W < 0 ? -1 : 1,
            Uv = vertex.Uv,
        };

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
    /// One cluster while its level is built: its triangles as mesh-local indices, the pool indices it draws with, and
    /// its row as the GPU reads it, short of where its indices land and what its parent group measures.
    /// </summary>
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
