using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GIRendering;

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
    Threads threads
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
    /// How many models were made or failed: when it changes, a renderer waiting for its model may draw now.
    /// </summary>
    public int SettledCount => _resident.Count + _failed.Count;

    public void Dispose()
    {
        foreach (ResidentModel resident in _resident.Values)
        {
            gpu.Destroy(resident.Acceleration);
        }

        gpu.Destroy(LeafClusters);
        gpu.Destroy(Vertices);
        gpu.Destroy(Indices);
        gpu.Destroy(Clusters);
    }

    public Result Create()
    {
        Result<GpuBuffer> vertices = gpu.CreateBuffer("Vertex pool", (long)VertexCapacity * GpuVertex.Bytes);
        if (!vertices.Ok)
            return Result.Failure(vertices.Error);

        Vertices = vertices.Value;
        Result<GpuBuffer> indices = gpu.CreateBuffer("Index pool", (long)IndexCapacity * sizeof(uint));
        if (!indices.Ok)
            return Result.Failure(indices.Error);

        Indices = indices.Value;
        Result<GpuBuffer> clusters = gpu.CreateBuffer("Cluster pool", (long)ClusterCapacity * GpuCluster.Bytes);
        if (!clusters.Ok)
            return Result.Failure(clusters.Error);

        Clusters = clusters.Value;
        if (gpu.Features.HasRayQuery)
        {
            Result<GpuBuffer> leaves = gpu.CreateBuffer("Leaf clusters", (long)ClusterCapacity * sizeof(uint));
            if (!leaves.Ok)
                return Result.Failure(leaves.Error);

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
    /// Whether the model is in the pools, copied up in the last upload pass at the latest, or could not be made and the
    /// failure model it is drawn as has arrived in its place, or has no place to arrive in.
    /// </summary>
    public bool HasArrived(Handle<Model> model)
    {
        if (_resident.ContainsKey(model.Id))
            return true;

        if (!HasFailed(model))
            return false;

        Handle<Model> failure = assets.GetFailure<Model, Model>();

        return failure == Handle<Model>.None || failure == model || HasArrived(failure);
    }

    /// <summary>
    /// Whether the model could not be made, and is drawn as the failure model.
    /// </summary>
    public bool HasFailed(Handle<Model> model) => _failed.Contains(model.Id);

    /// <summary>
    /// Copies what arrived since the last frame into the pools, in the frame's upload pass.
    /// </summary>
    public void Upload()
    {
        if (_arrived.Count == 0)
            return;

        graph.Use(Indices, GpuAccess.CopyDestination);
        graph.Use(Clusters, GpuAccess.CopyDestination);
        foreach ((MeshDerived derived, ResidentModel where) in _arrived)
        {
            graph.Upload(gpu, Vertices, derived.Vertices.AsSpan(), (ulong)where.VertexOffset * GpuVertex.Bytes);

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
    private GpuAcceleration CreateAcceleration(string name, MeshDerived made, ResidentModel where)
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
            Log.Warn($"{name} has no acceleration structure, so rays pass through it: {made2.Error}");

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

        graph.Upload(gpu, LeafClusters, CollectionsMarshal.AsSpan(leaves), (ulong)where.LeafOffset * sizeof(uint));
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

        ResidentModel where = new(
            (uint)_vertexCount,
            (uint)_indexCount,
            (uint)_clusterCount,
            (uint)made.Clusters.Length,
            made.BoundsCenter,
            made.BoundsRadius
        );
        if (gpu.Features.HasRayQuery)
            where = where with { LeafOffset = (uint)_leafCount, Acceleration = CreateAcceleration(name, made, where) };

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
/// Where a model is in the pools, and the sphere around it in its own frame; <paramref name="IsFailure"/> when it
/// stands in for a model that could not be made, to be drawn as a failure.
/// </summary>
internal readonly record struct ResidentModel(
    uint VertexOffset,
    uint IndexOffset,
    uint ClusterOffset,
    uint ClusterCount,
    Vector3 BoundsCenter,
    float BoundsRadius,
    bool IsFailure = false,
    uint LeafOffset = 0,
    GpuAcceleration Acceleration = default
);
