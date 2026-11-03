using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The three pools every drawn model lives in, vertices, indices and clusters, and which models are in them. A model
/// asked for is made by the preprocessor on a worker and copied into the pools in the next frame's upload pass; until
/// then it is not drawn. Pools are filled from the start and never compacted: a model stays resident for the run.
/// </summary>
internal sealed class GeometryPools(IGraphicsApi gpu, IMeshPreprocessor meshes, AssetDatabase assets, Threads threads)
    : IDisposable
{
    public const int VertexCapacity = 1 << 20;
    public const int IndexCapacity = 1 << 22;
    public const int ClusterCapacity = 1 << 16;

    private readonly Dictionary<ulong, ResidentModel> _resident = [];
    private readonly HashSet<ulong> _asked = [];
    private readonly List<(MeshDerived Derived, ResidentModel Where)> _arrived = [];
    private int _vertexCount;
    private int _indexCount;
    private int _clusterCount;

    public GpuBuffer Vertices { get; private set; }

    public GpuBuffer Indices { get; private set; }

    public GpuBuffer Clusters { get; private set; }

    public void Dispose()
    {
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

        return Result.Success();
    }

    /// <summary>
    /// Where the model is in the pools, or null while it is being made or when it could not be; the first ask starts
    /// making it.
    /// </summary>
    public ResidentModel? Get(Handle<Model> model)
    {
        if (_resident.TryGetValue(model.Id, out ResidentModel resident))
            return resident;

        if (_asked.Add(model.Id))
            LoadAsync(model).FireAndForget();

        return null;
    }

    /// <summary>
    /// Copies what arrived since the last frame into the pools, in the frame's upload pass.
    /// </summary>
    public void Upload(RenderGraph graph)
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
        }

        _arrived.Clear();
    }

    private async Task LoadAsync(Handle<Model> model)
    {
        Result<MeshDerived> derived = await meshes.GetAsync(model, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        string name = assets.GetPath(model) ?? $"model {model.Id}";
        if (!derived.Ok)
        {
            Log.Error($"{name} is not drawn: {derived.Error}");

            return;
        }

        MeshDerived made = derived.Value;
        bool fits =
            _vertexCount + made.Vertices.Length <= VertexCapacity
            && _indexCount + made.Indices.Length <= IndexCapacity
            && _clusterCount + made.Clusters.Length <= ClusterCapacity;
        if (!fits)
        {
            Log.Error($"{name} is not drawn: the geometry pools are full.");

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
        _vertexCount += made.Vertices.Length;
        _indexCount += made.Indices.Length;
        _clusterCount += made.Clusters.Length;
        _resident[model.Id] = where;
        _arrived.Add((made, where));
        int leafTriangles = 0;
        int roots = 0;
        foreach (GpuCluster cluster in made.Clusters)
        {
            if (cluster.Error == 0)
                leafTriangles += (int)cluster.TriangleCount;

            if (float.IsPositiveInfinity(cluster.ParentError))
                roots++;
        }

        Log.Info(
            $"{name}: {made.Vertices.Length} vertices, {made.Indices.Length / 3} triangles in {made.Clusters.Length} clusters, {leafTriangles} in the leaves, {roots} roots."
        );
    }
}

/// <summary>
/// Where a model is in the pools, and the sphere around it in its own frame.
/// </summary>
internal readonly record struct ResidentModel(
    uint VertexOffset,
    uint IndexOffset,
    uint ClusterOffset,
    uint ClusterCount,
    Vector3 BoundsCenter,
    float BoundsRadius
);
