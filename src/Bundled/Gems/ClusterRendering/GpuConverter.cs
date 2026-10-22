using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.ClusterRendering;

/// <summary>
/// Turns an asset into what the GPU holds of it, one <c>Convert</c> overload a kind, each a pure function a worker
/// runs: a model into packed clusters, levels and cards; a texture into a pool layer's compressed mips; a model's
/// coarsest meshes into its distance field (the field starts from the meshes rather than the model, as the model's
/// packing hands them over). The members of each conversion follow its <c>Convert</c>, in the order they run.
/// <para>
/// A vertex is five words: its position in 20 bits an axis, steps of its model's grid; its normal and its tangent each
/// folded onto an octahedron (Cigolle et al., "A Survey of Efficient Representations for Independent Unit Vectors",
/// 2014), the tangent's handedness in its top bit; and its uv as two halves. A triangle is one word, a byte for each
/// corner's place among its cluster's vertices.
/// </para>
/// </summary>
internal static class GpuConverter
{
    /// <summary>
    /// Words a vertex takes in <c>Geometry</c>.
    /// </summary>
    private const int VertexWords = 5;

    /// <summary>
    /// A grid step from the model's origin to its farthest vertex along an axis, as Core's clusters are made on.
    /// </summary>
    private const int PositionSteps = (1 << 19) - 1;

    /// <summary>
    /// Texels a side of a model's field (<see cref="GpuCache.FieldResolution"/>).
    /// </summary>
    private const int FieldResolution = GpuCache.FieldResolution;

    /// <summary>
    /// Texels past a surface its distance is exact to, of the field's coarsest step; and the margin around the model.
    /// The shaders' MeshFieldBand (Probes/GiJobs.hlsli).
    /// </summary>
    private const float FieldBand = 2.5f;

    /// <summary>
    /// What the BC7 encoder is told for every block: its own defaults, but every channel's error weighed alike, as most
    /// of the pools' textures are data rather than colour. Made, with the tables the encoder needs before its first
    /// block, the first time a texture is compressed, so a model's conversion never loads the encoder.
    /// </summary>
    private static readonly Bc7Enc.CompressBlockParams _blockOptions = MakeBlockOptions();

    /// <summary>
    /// A model packed for the pool: its clusters, or for one with none its meshes split into clusters; its levels; its
    /// cards, which take over from its clusters far off; and the coarsest triangles its field is made from.
    /// </summary>
    internal static PackedModel Convert(Model model)
    {
        float step = model.Clusters?.PositionStep ?? StepOver(model.Meshes);
        ClusterHierarchy hierarchy = model.Clusters ?? Flatten(model.Meshes, step);
        List<uint> words = [];
        List<GpuCluster> clusters = [];
        List<GpuLevel> levels = [];
        Vector4 bounds = default;
        Span<uint> packedVertex = stackalloc uint[VertexWords];
        Add(hierarchy, packedVertex);
        List<PackedCard> packedCards = PackCards(model, ref bounds);
        float clusterReach = ComputeReach(bounds, clusters);
        return new PackedModel(
            [.. words],
            [.. clusters],
            [.. levels],
            [.. packedCards],
            bounds,
            clusterReach,
            step,
            model.Cards?.Distance ?? 0,
            hierarchy.Cut(float.MaxValue));

        // A level is a run of one level's clusters, with the least error and the most parent error of any in it.
        void Add(ClusterHierarchy added, Span<uint> packed)
        {
            ReadOnlySpan<Vertex> vertices = added.Vertices.Span;
            ReadOnlySpan<byte> corners = added.Indices.Span;
            int lastLevel = -1;
            foreach (Cluster cluster in added.Clusters.Span)
            {
                uint firstVertex = (uint)words.Count;
                foreach (Vertex vertex in vertices.Slice(cluster.FirstVertex, cluster.VertexCount))
                {
                    PackVertex(vertex, step, packed);
                    words.AddRange(packed);
                }

                uint firstTriangle = (uint)words.Count;
                for (int t = 0; t < cluster.TriangleCount; t++)
                {
                    int at = cluster.FirstIndex + (t * 3);
                    words.Add(corners[at] | ((uint)corners[at + 1] << 8) | ((uint)corners[at + 2] << 16));
                }

                float parentError = float.IsPositiveInfinity(cluster.ParentError)
                    ? float.MaxValue
                    : cluster.ParentError;
                if (cluster.Level != lastLevel)
                {
                    levels.Add(new GpuLevel { FirstCluster = (uint)clusters.Count, MinError = float.MaxValue });
                    lastLevel = cluster.Level;
                }

                GpuLevel level = levels[^1];
                level.Count++;
                level.MinError = MathF.Min(level.MinError, cluster.Error);
                level.MaxParentError = MathF.Max(level.MaxParentError, parentError);
                levels[^1] = level;
                clusters.Add(new GpuCluster
                {
                    Bounds = cluster.Bounds,
                    ErrorBounds = cluster.ErrorBounds,
                    ParentErrorBounds = cluster.ParentErrorBounds,
                    Error = cluster.Error,
                    ParentError = parentError,
                    Vertices = firstVertex,
                    Triangles = firstTriangle,
                    Counts = GpuCluster.PackCounts(
                        cluster.TriangleCount,
                        cluster.VertexCount,
                        cluster.MaterialSlot,
                        cluster.Level),
                    Step = step,
                });
                if (cluster.Level == 0) bounds = Enclose(bounds, cluster.Bounds);
            }
        }
    }

    /// <summary>
    /// <paramref name="texture"/> as a texture pool layer of <paramref name="size"/> texels a side holds it: its
    /// <paramref name="levels"/> mips resampled from the texture's own (<see cref="Resample"/>), each compressed to
    /// BC7 (<see cref="Compress"/>).
    /// </summary>
    internal static byte[][] Convert(Texture texture, uint size, int levels)
        => Compress(Resample(texture, size, levels), size);

    /// <summary>
    /// A model's field from <paramref name="meshes"/>, its least detail: exact distances near each triangle, then which
    /// texels are inside, those a flood from the box's faces cannot reach past the surface; none for no triangles.
    /// </summary>
    internal static ModelField? Convert(IReadOnlyList<Mesh> meshes)
    {
        Vector3[] corners = GetCorners(meshes);
        if (corners.Length == 0) return null;

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vector3 corner in corners)
        {
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        // A margin the band wide all round, so the field holds the space just outside the surface, even of a flat
        // model.
        float largest = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        float margin = MathF.Max(FieldBand * largest / (FieldResolution - 1 - (2 * FieldBand)), 1e-3f);
        min -= new Vector3(margin);
        max += new Vector3(margin);
        Vector3 size = max - min;
        Vector3 step = size / (FieldResolution - 1);
        float band = FieldBand * MathF.Max(step.X, MathF.Max(step.Y, step.Z));

        const int Count = FieldResolution * FieldResolution * FieldResolution;
        float[] squared = new float[Count];
        int[] nearest = new int[Count];
        Array.Fill(squared, float.MaxValue);
        Array.Fill(nearest, -1);
        for (int triangle = 0; triangle < corners.Length / 3; triangle++)
        {
            RecordNearest(corners, triangle, min, step, band, squared, nearest);
        }

        // A texel a surface passes through is a wall the flood does not cross: every one within half its diagonal.
        float wall = 0.5f * step.Length();
        bool[] outside = Flood(squared, wall * wall);
        Half[] distances = new Half[Count];
        for (int index = 0; index < Count; index++)
        {
            float distance = MathF.Min(MathF.Sqrt(squared[index]), band);
            bool isOutside = outside[index];
            if (squared[index] < wall * wall)
            {
                (int x, int y, int z) = GetTexel(index);
                isOutside = IsInFront(min + (new Vector3(x, y, z) * step), corners, nearest[index]);
            }

            distances[index] = (Half)(isOutside ? distance : -distance);
        }

        return new ModelField(min, size, distances);
    }

    /// <summary>
    /// The model's cards as the GPU holds them, one per card quad, their atlases not yet in; <paramref name="bounds"/>
    /// grows to take each card in.
    /// </summary>
    private static List<PackedCard> PackCards(Model model, ref Vector4 bounds)
    {
        List<PackedCard> packedCards = [];
        foreach (Mesh mesh in model.Cards?.Meshes ?? [])
        {
            if (mesh.Card is not { } card) continue;

            ReadOnlySpan<Vertex> corners = mesh.Vertices.Span;
            for (int i = 0; i + 3 < corners.Length; i += 4)
            {
                Vertex corner = corners[i];
                packedCards.Add(new PackedCard(card, new GpuCard
                {
                    Centre = corner.Position,
                    HalfWidth = corner.Normal.X,
                    HalfHeight = corner.Normal.Y,
                    Turn = corner.Normal.Z,
                    Slot = (uint)mesh.MaterialSlot,
                    Surface = GpuContract.NoTexture,
                    Normals = GpuContract.NoTexture,
                }));
                float reach = new Vector2(corner.Normal.X, corner.Normal.Y).Length();
                bounds = Enclose(bounds, new Vector4(corner.Position, reach));
            }
        }

        return packedCards;
    }

    /// <summary>
    /// How far from the model's middle any of its clusters' error spheres reach. Each group's error sphere encloses its
    /// clusters', so a coarser cluster's, or its parent's, can reach past the bounds: an instance picks its levels of
    /// clusters by how near and far all of them reach, or it can pass over the level a cluster's own test wants and
    /// draw nothing there.
    /// </summary>
    private static float ComputeReach(Vector4 bounds, List<GpuCluster> clusters)
    {
        Vector3 middle = new(bounds.X, bounds.Y, bounds.Z);
        float clusterReach = bounds.W;
        foreach (GpuCluster cluster in clusters)
        {
            Vector4 own = cluster.ErrorBounds;
            Vector4 parent = cluster.ParentErrorBounds;
            clusterReach = MathF.Max(
                clusterReach,
                Vector3.Distance(middle, new Vector3(own.X, own.Y, own.Z)) + own.W);
            clusterReach = MathF.Max(
                clusterReach,
                Vector3.Distance(middle, new Vector3(parent.X, parent.Y, parent.Z)) + parent.W);
        }

        return clusterReach;
    }

    /// <summary>
    /// The grid step for <paramref name="meshes"/>: the farthest any vertex is from the origin along an axis, over the
    /// steps a position packs into.
    /// </summary>
    private static float StepOver(IReadOnlyList<Mesh> meshes)
    {
        float farthest = 0;
        foreach (Mesh mesh in meshes)
        {
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                Vector3 size = Vector3.Abs(vertex.Position);
                farthest = MathF.Max(farthest, MathF.Max(size.X, MathF.Max(size.Y, size.Z)));
            }
        }

        return MathF.Max(farthest, 1e-3f) / PositionSteps;
    }

    /// <summary>
    /// <paramref name="meshes"/> split into clusters as they come, of one level, never simplified: a model drawn as it
    /// is at every distance, on the grid <paramref name="step"/> apart.
    /// </summary>
    private static ClusterHierarchy Flatten(IReadOnlyList<Mesh> meshes, float step)
    {
        List<Vertex> vertices = [];
        List<byte> indices = [];
        List<Cluster> clusters = [];
        Dictionary<uint, int> local = [];
        foreach (Mesh mesh in meshes)
        {
            ReadOnlySpan<Vertex> source = mesh.Vertices.Span;
            ReadOnlySpan<uint> triangles = mesh.Indices.Span;
            int firstVertex = vertices.Count;
            int firstIndex = indices.Count;
            local.Clear();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int added = (local.ContainsKey(triangles[t]) ? 0 : 1) + (local.ContainsKey(triangles[t + 1]) ? 0 : 1)
                    + (local.ContainsKey(triangles[t + 2]) ? 0 : 1);
                if ((indices.Count - firstIndex) / 3 == ClusterHierarchy.MaxTriangles
                    || local.Count + added > ClusterHierarchy.MaxVertices)
                {
                    Close();
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    indices.Add(IndexWithin(triangles[t + corner], source, local, vertices));
                }
            }

            Close();

            void Close()
            {
                int count = (indices.Count - firstIndex) / 3;
                if (count > 0)
                {
                    clusters.Add(MakeFlatCluster(vertices, firstVertex, firstIndex, count, mesh.MaterialSlot));
                }

                firstVertex = vertices.Count;
                firstIndex = indices.Count;
                local.Clear();
            }
        }

        return new ClusterHierarchy
        {
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            Clusters = clusters.ToArray(),
            PositionStep = step
        };
    }

    /// <summary>
    /// The source vertex's index within the cluster being made, the vertex added to it the first time it is asked for.
    /// </summary>
    private static byte IndexWithin(
        uint index,
        ReadOnlySpan<Vertex> source,
        Dictionary<uint, int> local,
        List<Vertex> vertices)
    {
        if (local.TryGetValue(index, out int at)) return (byte)at;

        at = local.Count;
        local[index] = at;
        vertices.Add(source[(int)index]);
        return (byte)at;
    }

    /// <summary>
    /// A cluster of a flat hierarchy: full detail, no error and no parent, its sphere every bound it has.
    /// </summary>
    private static Cluster MakeFlatCluster(List<Vertex> vertices, int firstVertex, int firstIndex, int count, int slot)
    {
        Vector4 sphere = ComputeSphere(vertices, firstVertex, vertices.Count - firstVertex);
        return new Cluster
        {
            FirstVertex = firstVertex,
            VertexCount = vertices.Count - firstVertex,
            FirstIndex = firstIndex,
            TriangleCount = count,
            MaterialSlot = slot,
            Level = 0,
            Bounds = sphere,
            ErrorBounds = sphere,
            Error = 0,
            ParentErrorBounds = sphere,
            ParentError = float.PositiveInfinity,
        };
    }

    /// <summary>
    /// Packs a vertex into <paramref name="words"/>: its position as steps of <paramref name="step"/>, its normal and
    /// tangent folded onto an octahedron, its uv as halves.
    /// </summary>
    private static void PackVertex(in Vertex vertex, float step, Span<uint> words)
    {
        uint x = Steps(vertex.Position.X);
        uint y = Steps(vertex.Position.Y);
        uint z = Steps(vertex.Position.Z);
        words[0] = x | (y << 20);
        words[1] = (y >> 12) | (z << 8);
        Vector2 normal = EncodeOctahedral(vertex.Normal);
        words[2] = ToUnorm(normal.X, 16) | (ToUnorm(normal.Y, 16) << 16);
        Vector2 tangent = EncodeOctahedral(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z));
        words[3] = ToUnorm(tangent.X, 16) | (ToUnorm(tangent.Y, 15) << 16) | (vertex.Tangent.W < 0 ? 1u << 31 : 0);
        words[4] = BitConverter.HalfToUInt16Bits((Half)vertex.Uv.X)
            | ((uint)BitConverter.HalfToUInt16Bits((Half)vertex.Uv.Y) << 16);

        // Two's complement in 20 bits: a negative step's high bits are cut off, and the shader sign-extends them back.
        uint Steps(float value)
            => unchecked((uint)(int)Math.Clamp(Math.Round(value / (double)step), -PositionSteps, PositionSteps))
                & 0xFFFFF;
    }

    /// <summary>
    /// A direction folded onto an octahedron and unfolded onto a square, 0 to 1 each way (Cigolle et al. 2014).
    /// </summary>
    private static Vector2 EncodeOctahedral(Vector3 direction)
    {
        float sum = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
        if (sum <= 0) return new Vector2(0.5f, 0.5f);

        Vector3 folded = direction / sum;
        Vector2 square = new(folded.X, folded.Y);
        if (folded.Z < 0)
        {
            square = new Vector2(
                (1 - MathF.Abs(folded.Y)) * (folded.X >= 0 ? 1 : -1),
                (1 - MathF.Abs(folded.X)) * (folded.Y >= 0 ? 1 : -1));
        }

        return (square * 0.5f) + new Vector2(0.5f);
    }

    private static uint ToUnorm(float value, int bits)
        => (uint)MathF.Round(Math.Clamp(value, 0, 1) * ((1 << bits) - 1));

    private static Vector4 ComputeSphere(List<Vertex> vertices, int first, int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int i = first; i < first + count; i++)
        {
            min = Vector3.Min(min, vertices[i].Position);
            max = Vector3.Max(max, vertices[i].Position);
        }

        Vector3 centre = (min + max) / 2;
        float radius = 0;
        for (int i = first; i < first + count; i++)
        {
            radius = MathF.Max(radius, Vector3.Distance(centre, vertices[i].Position));
        }

        return new Vector4(centre, radius);
    }

    /// <summary>
    /// The sphere around both; <paramref name="a"/> may be none.
    /// </summary>
    private static Vector4 Enclose(Vector4 a, Vector4 b)
    {
        if (a.W <= 0) return b;

        Vector3 from = new(a.X, a.Y, a.Z);
        Vector3 to = new(b.X, b.Y, b.Z);
        float apart = Vector3.Distance(from, to);
        if (apart + b.W <= a.W) return a;

        if (apart + a.W <= b.W) return b;

        float radius = (apart + a.W + b.W) / 2;
        Vector3 centre = from + ((to - from) * ((radius - a.W) / MathF.Max(apart, 1e-6f)));
        return new Vector4(centre, radius);
    }

    /// <summary>
    /// The first <paramref name="count"/> mips of a <paramref name="size"/> square from the texture's levels: each from
    /// the smallest of the texture's levels at least as big, a box of texels averaged where it shrinks and the nearest
    /// texel where it grows, so a small pattern stays crisp. Averaging sRGB bytes is not quite averaging light, which a
    /// mip of a colour texture does not show.
    /// </summary>
    private static byte[][] Resample(Texture texture, uint size, int count)
    {
        byte[][] levels = new byte[count][];
        for (int level = 0; level < count; level++)
        {
            int side = (int)Math.Max(1, size >> level);
            TextureLevel source = texture.Levels[0];
            foreach (TextureLevel candidate in texture.Levels)
            {
                if (candidate.Width >= side && candidate.Height >= side) source = candidate;
            }

            levels[level] = ResampleLevel(
                texture.Pixels.Span.Slice(source.Offset, source.Size),
                source.Width,
                source.Height,
                side);
        }

        return levels;
    }

    private static byte[] ResampleLevel(ReadOnlySpan<byte> pixels, int width, int height, int side)
    {
        byte[] resampled = new byte[side * side * 4];
        int boxX = Math.Max(1, width / side);
        int boxY = Math.Max(1, height / side);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int fromX = (int)((long)x * width / side);
                int fromY = (int)((long)y * height / side);
                for (int channel = 0; channel < 4; channel++)
                {
                    resampled[(((y * side) + x) * 4) + channel] =
                        (byte)(SumBox(pixels, width, height, fromX, fromY, boxX, boxY, channel) / (boxX * boxY));
                }
            }
        }

        return resampled;
    }

    /// <summary>
    /// The sum of one channel over the box of texels from (<paramref name="fromX"/>, <paramref name="fromY"/>), clamped
    /// to the image's edge.
    /// </summary>
    private static int SumBox(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int fromX,
        int fromY,
        int boxX,
        int boxY,
        int channel)
    {
        int sum = 0;
        for (int dy = 0; dy < boxY; dy++)
        {
            for (int dx = 0; dx < boxX; dx++)
            {
                int texel = (Math.Min(fromY + dy, height - 1) * width) + Math.Min(fromX + dx, width - 1);
                sum += pixels[(texel * 4) + channel];
            }
        }

        return sum;
    }

    /// <summary>
    /// Each level of a <paramref name="size"/> square compressed to BC7, a row of blocks to a worker.
    /// </summary>
    private static byte[][] Compress(byte[][] levels, uint size)
    {
        byte[][] compressed = new byte[levels.Length][];
        for (int level = 0; level < levels.Length; level++)
        {
            int side = (int)Math.Max(1, size >> level);
            byte[] pixels = levels[level];
            byte[] blocks = new byte[side * side];
            Parallel.For(0, side / 4, row => CompressRow(pixels, blocks, side, row));
            compressed[level] = blocks;
        }

        return compressed;
    }

    /// <summary>
    /// A row of the 4 x 4 blocks of a <paramref name="side"/> square into <paramref name="blocks"/>, sixteen bytes
    /// each, the encoder taking a block's texels together.
    /// </summary>
    private static unsafe void CompressRow(byte[] pixels, byte[] blocks, int side, int row)
    {
        Bc7Enc.CompressBlockParams options = _blockOptions;
        Span<byte> texels = stackalloc byte[64];
        for (int column = 0; column < side / 4; column++)
        {
            for (int y = 0; y < 4; y++)
            {
                pixels.AsSpan(((((row * 4) + y) * side) + (column * 4)) * 4, 16).CopyTo(texels[(y * 16)..]);
            }

            fixed (byte* block = &blocks[((row * (side / 4)) + column) * 16])
            fixed (byte* source = texels)
            {
                Bc7Enc.CompressBlock(block, source, &options);
            }
        }
    }

    private static unsafe Bc7Enc.CompressBlockParams MakeBlockOptions()
    {
        Bc7Enc.CompressBlockInit();
        Bc7Enc.CompressBlockParams options = new()
        {
            MMaxPartitionsMode = Bc7Enc.MaxPartitions1,
            MTryLeastSquares = 1,
            MModePartitionEstimationFilterbank = 1,
            MUseMode5ForAlpha = 1,
            MUseMode7ForAlpha = 1,
        };
        new Span<uint>(options.MWeights, 4).Fill(1);
        return options;
    }

    /// <summary>
    /// The triangle as the nearest surface of every texel within <paramref name="band"/> of it where it is nearer than
    /// what the texel had: the squared distance, and which triangle.
    /// </summary>
    private static void RecordNearest(
        ReadOnlySpan<Vector3> corners,
        int triangle,
        Vector3 min,
        Vector3 step,
        float band,
        float[] squared,
        int[] nearest)
    {
        Vector3 a = corners[triangle * 3];
        Vector3 b = corners[(triangle * 3) + 1];
        Vector3 c = corners[(triangle * 3) + 2];
        (int x0, int x1) = GetTexelRange(
            MathF.Min(a.X, MathF.Min(b.X, c.X)) - band,
            MathF.Max(a.X, MathF.Max(b.X, c.X)) + band,
            min.X,
            step.X);
        (int y0, int y1) = GetTexelRange(
            MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - band,
            MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + band,
            min.Y,
            step.Y);
        (int z0, int z1) = GetTexelRange(
            MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - band,
            MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + band,
            min.Z,
            step.Z);
        for (int z = z0; z <= z1; z++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Vector3 point = min + (new Vector3(x, y, z) * step);
                    float distance = Vector3.DistanceSquared(point, FindClosestOnTriangle(point, a, b, c));
                    int index = x + (FieldResolution * (y + (FieldResolution * z)));
                    if (distance >= squared[index]) continue;

                    squared[index] = distance;
                    nearest[index] = triangle;
                }
            }
        }
    }

    /// <summary>
    /// Every triangle of <paramref name="meshes"/>, three corners each.
    /// </summary>
    private static Vector3[] GetCorners(IReadOnlyList<Mesh> meshes)
    {
        int count = 0;
        foreach (Mesh mesh in meshes)
        {
            count += mesh.Indices.Length / 3 * 3;
        }

        Vector3[] corners = new Vector3[count];
        int at = 0;
        foreach (Mesh mesh in meshes)
        {
            ReadOnlySpan<Vertex> vertices = mesh.Vertices.Span;
            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            for (int i = 0; i < indices.Length / 3 * 3; i++)
            {
                corners[at++] = vertices[(int)indices[i]].Position;
            }
        }

        return corners;
    }

    /// <summary>
    /// The texels along one axis from <paramref name="low"/> to <paramref name="high"/>, inside the field.
    /// </summary>
    private static (int First, int Last) GetTexelRange(float low, float high, float origin, float step)
    {
        int first = Math.Max(0, (int)MathF.Ceiling((low - origin) / step));
        int last = Math.Min(FieldResolution - 1, (int)MathF.Floor((high - origin) / step));
        return (first, last);
    }

    /// <summary>
    /// Which texels are outside: those reached from the box's faces stepping only between neighbours no surface passes
    /// through (<paramref name="wall"/>, the squared distance under which one does).
    /// </summary>
    private static bool[] Flood(float[] squared, float wall)
    {
        bool[] outside = new bool[squared.Length];
        Queue<int> next = new();
        for (int index = 0; index < squared.Length; index++)
        {
            (int x, int y, int z) = GetTexel(index);
            bool isFace = x == 0 || y == 0 || z == 0 || x == FieldResolution - 1 || y == FieldResolution - 1
                || z == FieldResolution - 1;
            if (isFace && squared[index] >= wall)
            {
                outside[index] = true;
                next.Enqueue(index);
            }
        }

        Span<int> neighbours = stackalloc int[6];
        while (next.TryDequeue(out int index))
        {
            (int x, int y, int z) = GetTexel(index);
            int found = 0;
            if (x > 0) neighbours[found++] = index - 1;

            if (x < FieldResolution - 1) neighbours[found++] = index + 1;

            if (y > 0) neighbours[found++] = index - FieldResolution;

            if (y < FieldResolution - 1) neighbours[found++] = index + FieldResolution;

            if (z > 0) neighbours[found++] = index - (FieldResolution * FieldResolution);

            if (z < FieldResolution - 1) neighbours[found++] = index + (FieldResolution * FieldResolution);

            foreach (int neighbour in neighbours[..found])
            {
                if (!outside[neighbour] && squared[neighbour] >= wall)
                {
                    outside[neighbour] = true;
                    next.Enqueue(neighbour);
                }
            }
        }

        return outside;
    }

    /// <summary>
    /// A field's texel by its index, x fastest.
    /// </summary>
    private static (int X, int Y, int Z) GetTexel(int index) => (
        index % FieldResolution,
        index / FieldResolution % FieldResolution,
        index / (FieldResolution * FieldResolution));

    /// <summary>
    /// Whether <paramref name="point"/> is on the side of its nearest triangle its winding faces, as a texel the
    /// surface passes through is told inside or out.
    /// </summary>
    private static bool IsInFront(Vector3 point, Vector3[] corners, int triangle)
    {
        if (triangle < 0) return true;

        Vector3 a = corners[triangle * 3];
        Vector3 b = corners[(triangle * 3) + 1];
        Vector3 c = corners[(triangle * 3) + 2];
        Vector3 facing = Vector3.Cross(b - a, c - a);
        return Vector3.Dot(point - FindClosestOnTriangle(point, a, b, c), facing) >= 0;
    }

    /// <summary>
    /// The point of triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> nearest
    /// <paramref name="p"/> (Ericson 2004, "Real-Time Collision Detection", 5.1.5).
    /// </summary>
    private static Vector3 FindClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + (ab * (d1 / (d1 - d3)));

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + (ac * (d2 / (d2 - d6)));

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + ((c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6))));

        float denominator = va + vb + vc;
        if (MathF.Abs(denominator) < 1e-20f) return a;

        float v = vb / denominator;
        float w = vc / denominator;
        return a + (ab * v) + (ac * w);
    }
}
