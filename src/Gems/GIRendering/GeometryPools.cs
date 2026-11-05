using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// The three pools every drawn model lives in, vertices, indices and clusters, and which models are in them. A model
/// asked for is made by the preprocessor on a worker and copied into the pools in the next frame's upload pass; until
/// then it is not drawn, and one that could not be made is drawn as the failure model. Pools are filled from the
/// start and never compacted: a model stays resident for the run.
/// </summary>
internal sealed class GeometryPools(
    IGraphicsApi gpu,
    RenderGraph graph,
    IMeshPreprocessor meshes,
    Assets assets,
    Threads threads,
    GpuPipelines pipelines
) : IDisposable
{
    public const int VertexCapacity = 1 << 20;
    public const int IndexCapacity = 1 << 22;
    public const int ClusterCapacity = 1 << 16;

    private readonly Dictionary<ulong, ResidentModel> _resident = [];
    private readonly HashSet<ulong> _asked = [];
    private readonly HashSet<ulong> _failed = [];
    private readonly List<(MeshDerived Derived, ResidentModel Where)> _arrived = [];
    private int _vertexCount;
    private int _indexCount;
    private int _clusterCount;
    private int _leafCount;

    public GpuBuffer Vertices { get; private set; }

    public GpuBuffer Indices { get; private set; }

    public GpuBuffer Clusters { get; private set; }

    /// <summary>
    /// The leaf clusters of every resident model, in the order its acceleration structure's geometries name them;
    /// none on a GPU that traces no rays.
    /// </summary>
    public GpuBuffer LeafClusters { get; private set; }

    /// <summary>
    /// Whether a model arrived since the last frame: what rebuilds the instances' acceleration structure.
    /// </summary>
    public bool HasArrivals => _arrived.Count > 0;

    /// <summary>
    /// The global distance field, which every resident model's field joins.
    /// </summary>
    public DistanceField Field { get; } = new(gpu, graph, pipelines);

    public void Dispose()
    {
        Field.Dispose();
        foreach (ResidentModel resident in _resident.Values)
        {
            if (!resident.Acceleration.IsNone)
                gpu.Destroy(resident.Acceleration);
        }

        if (!LeafClusters.IsNone)
            gpu.Destroy(LeafClusters);

        if (!Vertices.IsNone)
            gpu.Destroy(Vertices);

        if (!Indices.IsNone)
            gpu.Destroy(Indices);

        if (!Clusters.IsNone)
            gpu.Destroy(Clusters);
    }

    public Result Create()
    {
        Result<GpuBuffer> vertices = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)VertexCapacity * GpuVertex.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Vertex pool",
            }
        );
        if (!vertices.Ok)
            return Result.Failure($"the vertex pool was not made: {vertices.Error}");

        Vertices = vertices.Value;
        Result<GpuBuffer> indices = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)IndexCapacity * sizeof(uint),
                Usage = BufferUsages.Storage,
                Name = "Index pool",
            }
        );
        if (!indices.Ok)
            return Result.Failure($"the index pool was not made: {indices.Error}");

        Indices = indices.Value;
        Result<GpuBuffer> clusters = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)ClusterCapacity * GpuCluster.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Cluster pool",
            }
        );
        if (!clusters.Ok)
            return Result.Failure($"the cluster pool was not made: {clusters.Error}");

        Clusters = clusters.Value;
        if (gpu.Features.HasRayQuery)
        {
            Result<GpuBuffer> leaves = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = (long)ClusterCapacity * sizeof(uint),
                    Usage = BufferUsages.Storage,
                    Name = "Leaf clusters",
                }
            );
            if (!leaves.Ok)
                return Result.Failure($"the leaf cluster table was not made: {leaves.Error}");

            LeafClusters = leaves.Value;
        }

        return Result.Success();
    }

    /// <summary>
    /// Where the model is in the pools, or null while it is being made; the first ask starts making it. A model that
    /// could not be made is the failure model, marked so.
    /// </summary>
    public ResidentModel? Get(Handle<Model> model)
    {
        if (_failed.Contains(model.Id))
        {
            Handle<Model> failure = assets.GetFailure<Model, Model>();
            if (failure == Handle<Model>.None || _failed.Contains(failure.Id))
                return null;

            return Get(failure) is { } stand ? stand with { IsFailure = true } : null;
        }

        if (_resident.TryGetValue(model.Id, out ResidentModel resident))
            return resident;

        if (_asked.Add(model.Id))
            LoadAsync(model).FireAndForget();

        return null;
    }

    /// <summary>
    /// Copies what arrived since the last frame into the pools, in the frame's upload pass.
    /// </summary>
    public void Upload()
    {
        if (_arrived.Count == 0)
            return;

        graph.Use(Vertices, GpuAccess.CopyDestination);
        graph.Use(Indices, GpuAccess.CopyDestination);
        graph.Use(Clusters, GpuAccess.CopyDestination);
        foreach ((MeshDerived derived, ResidentModel where) in _arrived)
        {
            int vertexBytes = derived.Vertices.Length * GpuVertex.Bytes;
            GpuUpload vertices = gpu.BeginUpload(vertexBytes);
            MemoryMarshal.AsBytes(derived.Vertices.AsSpan()).CopyTo(vertices.Bytes);
            graph.Recorder.CopyBuffer(
                vertices.Buffer,
                vertices.Offset,
                Vertices,
                (ulong)where.VertexOffset * GpuVertex.Bytes,
                (ulong)vertexBytes
            );

            // Indices point into the whole pool, and clusters into the whole index pool.
            int indexBytes = derived.Indices.Length * sizeof(uint);
            GpuUpload indices = gpu.BeginUpload(indexBytes);
            Span<uint> rebased = MemoryMarshal.Cast<byte, uint>(indices.Bytes);
            for (int i = 0; i < derived.Indices.Length; i++)
            {
                rebased[i] = derived.Indices[i] + where.VertexOffset;
            }

            graph.Recorder.CopyBuffer(
                indices.Buffer,
                indices.Offset,
                Indices,
                (ulong)where.IndexOffset * sizeof(uint),
                (ulong)indexBytes
            );

            int clusterBytes = derived.Clusters.Length * GpuCluster.Bytes;
            GpuUpload clusters = gpu.BeginUpload(clusterBytes);
            Span<GpuCluster> rows = MemoryMarshal.Cast<byte, GpuCluster>(clusters.Bytes);
            for (int i = 0; i < derived.Clusters.Length; i++)
            {
                GpuCluster cluster = derived.Clusters[i];
                cluster.IndexOffset += where.IndexOffset;
                rows[i] = cluster;
            }

            graph.Recorder.CopyBuffer(
                clusters.Buffer,
                clusters.Offset,
                Clusters,
                (ulong)where.ClusterOffset * GpuCluster.Bytes,
                (ulong)clusterBytes
            );
            UploadLeaves(derived, where);
        }

        _arrived.Clear();
    }

    /// <summary>
    /// The model's acceleration structure over its leaf clusters, a geometry each, and the leaves counted into the
    /// leaf table; none when the structure could not be made.
    /// </summary>
    private GpuAcceleration MakeAcceleration(string name, MeshDerived made, ResidentModel where)
    {
        List<TriangleGeometry> geometries = [];
        foreach (GpuCluster cluster in made.Clusters)
        {
            if (cluster.Error != 0)
                continue;

            geometries.Add(
                new TriangleGeometry(
                    Vertices,
                    0,
                    GpuVertex.Bytes,
                    VertexCapacity,
                    Indices,
                    (ulong)(where.IndexOffset + cluster.IndexOffset) * sizeof(uint),
                    cluster.TriangleCount
                )
            );
        }

        if (geometries.Count == 0 || _leafCount + geometries.Count > ClusterCapacity)
            return GpuAcceleration.None;

        Result<GpuAcceleration> made2 = gpu.CreateAcceleration(
            new BottomLevelDescription { Geometries = [.. geometries], Name = $"{name} acceleration" }
        );
        if (!made2.Ok)
        {
            Log.Warn($"{name} is traced through the field alone: {made2.Error}");

            return GpuAcceleration.None;
        }

        _leafCount += geometries.Count;

        return made2.Value;
    }

    /// <summary>
    /// The model's leaf clusters into the leaf table and its acceleration structure built, after its pools landed.
    /// </summary>
    private void UploadLeaves(MeshDerived derived, ResidentModel where)
    {
        if (where.Acceleration.IsNone)
            return;

        List<uint> leaves = [];
        for (int i = 0; i < derived.Clusters.Length; i++)
        {
            GpuCluster cluster = derived.Clusters[i];
            if (cluster.Error == 0)
                leaves.Add(where.ClusterOffset + (uint)i);
        }

        int bytes = leaves.Count * sizeof(uint);
        GpuUpload upload = gpu.BeginUpload(bytes);
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(leaves)).CopyTo(upload.Bytes);
        graph.Use(LeafClusters, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(
            upload.Buffer,
            upload.Offset,
            LeafClusters,
            (ulong)where.LeafOffset * sizeof(uint),
            (ulong)bytes
        );
        graph.Recorder.BuildAcceleration(where.Acceleration);
    }

    private async Task LoadAsync(Handle<Model> model)
    {
        Result<MeshDerived> derived = await meshes.GetAsync(model, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        string name = assets.GetPath(model) ?? $"model {model.Id}";
        if (!derived.Ok)
        {
            Log.Error($"{name} is drawn as the failure model: {derived.Error}");
            _failed.Add(model.Id);

            return;
        }

        MeshDerived made = derived.Value;
        bool fits =
            _vertexCount + made.Vertices.Length <= VertexCapacity
            && _indexCount + made.Indices.Length <= IndexCapacity
            && _clusterCount + made.Clusters.Length <= ClusterCapacity;
        if (!fits)
        {
            Log.Error($"{name} is drawn as the failure model: the geometry pools are full.");
            _failed.Add(model.Id);

            return;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (GpuVertex vertex in made.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        ResidentModel where = new(
            (uint)_vertexCount,
            (uint)_indexCount,
            (uint)_clusterCount,
            (uint)made.Clusters.Length,
            made.BoundsCenter,
            made.BoundsRadius,
            made.Vertices.IsEmpty ? Vector3.Zero : min,
            made.Vertices.IsEmpty ? Vector3.Zero : max,
            FieldSlot: made.Field.IsDefaultOrEmpty ? 0 : Field.Add(made)
        );
        if (gpu.Features.HasRayQuery)
            where = where with { LeafOffset = (uint)_leafCount, Acceleration = MakeAcceleration(name, made, where) };

        _vertexCount += made.Vertices.Length;
        _indexCount += made.Indices.Length;
        _clusterCount += made.Clusters.Length;
        _resident[model.Id] = where;
        _arrived.Add((made, where));
        int leafTriangles = 0;
        foreach (GpuCluster cluster in made.Clusters)
        {
            if (cluster.Error == 0)
                leafTriangles += (int)cluster.TriangleCount;
        }

        Log.Info(
            $"{name}: {made.Vertices.Length} vertices, {made.Indices.Length / 3} triangles in {made.Clusters.Length} clusters, {leafTriangles} in the leaves."
        );
    }
}

/// <summary>
/// Where a model is in the pools, and the sphere and the box around it in its own frame; <paramref name="IsFailure"/>
/// when it stands in for a model that could not be made, to be drawn as a failure; <paramref name="FieldSlot"/> its
/// field's slot in the atlas plus one, 0 for none.
/// </summary>
internal readonly record struct ResidentModel(
    uint VertexOffset,
    uint IndexOffset,
    uint ClusterOffset,
    uint ClusterCount,
    Vector3 BoundsCenter,
    float BoundsRadius,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    bool IsFailure = false,
    uint FieldSlot = 0,
    uint LeafOffset = 0,
    GpuAcceleration Acceleration = default
);
