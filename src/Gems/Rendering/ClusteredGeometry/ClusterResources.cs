using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Each model on the GPU as the cluster renderer draws it, kept row for row with the view's model table: a model that
/// comes into a row is read from the cache on a worker, as its preprocessor made it, settled into the pools of
/// vertices, indices and clusters, which grow to hold it, the impostor table and its atlases, and copied up in the
/// frame it arrives; one that leaves its row gives its ranges back to the free lists, and its atlases are no longer
/// asked for, so the GPU provider frees them. A row is empty, and its instances draw nothing, until its model arrives.
/// The pools and the atlases are asked of the graph every frame, so the GPU provider keeps what they hold.
/// </summary>
internal sealed class ClusterResources(ClusterPreprocessor clusters, AssetManager assets, Threads threads)
{
    private const int ImpostorCapacity = 1024;

    /// <summary>
    /// What each row of the view's model table is drawn as, by its model index.
    /// </summary>
    private readonly List<Row> _rows = [];

    /// <summary>
    /// The models read since the last frame, with the place they were read for, which the next
    /// <see cref="Prepare"/> settles.
    /// </summary>
    private readonly List<(int Index, ModelPlace Place, Result<ModelClusters> Made)> _madeModels = [];

    /// <summary>
    /// The models of this frame's <see cref="_madeModels"/> that took their ranges, by the row they took them for, to
    /// copy up once the pools are asked for at their size.
    /// </summary>
    private readonly List<(int Index, ModelClusters Made)> _placedModels = [];

    // The impostor table's rows; row 0 is none, so it is taken from the start.
    private readonly FreeList _impostorRows = new(ImpostorCapacity, 1);

    // About 3 MiB in all at the start; each grows with the scene.
    private readonly BufferPool _vertices = new(
        GpuDescriptions.GetBuffer("Cluster vertex pool", 0),
        GpuVertex.Bytes,
        1 << 16
    );

    private readonly BufferPool _indices = new(
        GpuDescriptions.GetBuffer("Cluster index pool", 0),
        sizeof(uint),
        1 << 18
    );

    private readonly BufferPool _clusters = new(
        GpuDescriptions.GetBuffer("Cluster pool", 0),
        GpuCluster.Bytes,
        1 << 12
    );

    /// <summary>
    /// Whether a model in the view's table is still being read, and its row draws nothing yet.
    /// </summary>
    public bool IsLoading
    {
        get
        {
            foreach (Row row in _rows)
            {
                if (row.State == RowState.Loading)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The pools and tables asked for, and kept row for row with <paramref name="models"/>: each row whose model changed
    /// emptied and its model asked for, each model read since the last frame copied up, the pools grown where it does
    /// not fit, and every resident impostor's atlases asked for, which are what it returns beside the pools.
    /// </summary>
    public Result<ClusterPools> Prepare(RenderGraph graph, ReadOnlySpan<ModelPlace> models)
    {
        Result<GpuBuffer> table = graph.Buffer(
            GpuDescriptions.GetBuffer("Cluster model table", (long)models.Length * GpuClusterModel.Bytes)
        );
        Result<GpuBuffer> impostors = graph.Buffer(
            GpuDescriptions.GetBuffer("Impostor table", (long)ImpostorCapacity * GpuImpostor.Bytes)
        );
        if (Result.FindFailure(table, impostors) is { Ok: false } failedTable)
            return Result.Failure(failedTable.Error);

        // A row whose model changed draws nothing until what it holds now arrives.
        while (_rows.Count < models.Length)
        {
            _rows.Add(default);
        }

        for (int index = 0; index < models.Length; index++)
        {
            if (_rows[index].Place == models[index])
                continue;

            // The row's ranges given back to the free lists; its atlases go once no frame asks for them.
            Row old = _rows[index];
            if (old.State == RowState.Ready)
            {
                _vertices.Free((int)old.VertexOffset, (int)old.VertexCount);
                _indices.Free((int)old.IndexOffset, (int)old.IndexCount);
                _clusters.Free((int)old.ClusterOffset, (int)old.ClusterCount);
                if (old.ImpostorRow != 0)
                    _impostorRows.Free((int)old.ImpostorRow, 1);
            }

            ModelPlace place = models[index];
            bool isEmpty = place.Model == Handle<Model>.None;
            _rows[index] = new Row { Place = place, State = isEmpty ? RowState.Empty : RowState.Loading };
            graph.Upload(table.Value, [default(GpuClusterModel)], (ulong)index * GpuClusterModel.Bytes);
            if (!isEmpty)
                LoadAsync(index, place).FireAndForget();
        }

        // Each model read since the last frame takes its ranges, which may grow the pools. A model read for a row that
        // has since changed, or that a read before it settled, is dropped; the row asked for what it holds now.
        foreach ((int index, ModelPlace place, Result<ModelClusters> made) in _madeModels)
        {
            if (_rows[index].Place != place || _rows[index].State != RowState.Loading)
                continue;

            if (!made.Ok)
            {
                Log.Error($"{GetName(place)} draws no clusters: {made.Error}");
                _rows[index] = new Row { Place = place, State = RowState.Empty };
                continue;
            }

            ModelClusters model = made.Value;
            _rows[index] = new Row
            {
                Place = place,
                State = RowState.Ready,
                VertexOffset = (uint)_vertices.Allocate(model.Vertices.Length),
                VertexCount = (uint)model.Vertices.Length,
                IndexOffset = (uint)_indices.Allocate(model.Indices.Length),
                IndexCount = (uint)model.Indices.Length,
                ClusterOffset = (uint)_clusters.Allocate(model.Clusters.Length),
                ClusterCount = (uint)model.Clusters.Length,
            };
            _placedModels.Add((index, model));
        }

        _madeModels.Clear();

        // Asked for once the ranges are taken and before anything is copied into them, as a pool that grew is a new
        // buffer, which the GPU provider fills with what the old one held.
        Result<GpuBuffer> vertices = _vertices.GetBuffer(graph);
        Result<GpuBuffer> indices = _indices.GetBuffer(graph);
        Result<GpuBuffer> clusters = _clusters.GetBuffer(graph);
        if (Result.FindFailure(vertices, indices, clusters) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        ClusterPools pools = new(vertices.Value, indices.Value, clusters.Value, table.Value, impostors.Value, []);
        foreach ((int index, ModelClusters made) in _placedModels)
        {
            Upload(graph, pools, index, made);
        }

        _placedModels.Clear();

        // Every resident impostor's atlases, asked of the graph, so the GPU provider keeps them and a pass that samples
        // them names them in its reads.
        ImmutableArray<GpuResource>.Builder atlases = ImmutableArray.CreateBuilder<GpuResource>();
        foreach (Row row in _rows)
        {
            if (row.Impostor is not { } atlas || row.ImpostorUv is not { } uvAtlas)
                continue;

            if (graph.Texture(atlas) is { Ok: true } surface)
                atlases.Add(surface.Value);

            if (graph.Texture(uvAtlas) is { Ok: true } uvs)
                atlases.Add(uvs.Value);
        }

        return Result.Success(pools with { Atlases = atlases.ToImmutable() });
    }

    /// <summary>
    /// An impostor atlas of <see cref="ImpostorBuilder.Size"/> texels a side, in <paramref name="format"/>.
    /// </summary>
    private static TextureDescription GetAtlasDescription(string name, GpuFormat format) =>
        GpuDescriptions.GetTexture(
            name,
            ImpostorBuilder.Size,
            ImpostorBuilder.Size,
            format,
            TextureUsages.Sampled | TextureUsages.CopyDestination
        );

    private string GetName(ModelPlace place) => assets.GetPath(place.Model) ?? $"model {place.Model.Id}";

    /// <summary>
    /// The model's clusters, as its preprocessor made them, settled by the next <see cref="Prepare"/>.
    /// </summary>
    private async Task LoadAsync(int index, ModelPlace place)
    {
        Result<ModelClusters> made = await assets
            .LoadPreprocessedAsync(place.Model, clusters, CancellationToken.None)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        _madeModels.Add((index, place, made));
    }

    /// <summary>
    /// The model's clusters copied into the ranges its row took in <paramref name="pools"/>, its impostor given a row of
    /// the impostor table and its atlases when it baked one, and its row of the model table written.
    /// </summary>
    private void Upload(RenderGraph graph, in ClusterPools pools, int index, ModelClusters made)
    {
        Row row = _rows[index];
        ModelPlace place = row.Place;
        graph.Upload(pools.Vertices, made.Vertices.AsSpan(), (ulong)row.VertexOffset * GpuVertex.Bytes);

        // An index under the model's own vertex count is into the world's vertex pool, where its model's vertices start
        // at its place; the rest are into this pool's copies.
        uint modelVertexCount = (uint)made.ModelVertexCount;
        Span<uint> rebased = graph.Upload<uint>(
            pools.Indices,
            made.Indices.Length,
            (ulong)row.IndexOffset * sizeof(uint)
        );
        for (int i = 0; i < made.Indices.Length; i++)
        {
            uint vertex = made.Indices[i];
            rebased[i] =
                vertex < modelVertexCount ? place.VertexOffset + vertex : row.VertexOffset + vertex - modelVertexCount;
        }

        // A cluster's triangles are all the model's own, a leaf's, or all copies, a level's above.
        Span<GpuCluster> rows = graph.Upload<GpuCluster>(
            pools.Clusters,
            made.Clusters.Length,
            (ulong)row.ClusterOffset * GpuCluster.Bytes
        );
        for (int i = 0; i < made.Clusters.Length; i++)
        {
            GpuCluster cluster = made.Clusters[i];
            bool isLeaf = cluster.TriangleCount == 0 || made.Indices[(int)cluster.IndexOffset] < modelVertexCount;
            cluster.IsCopy = isLeaf ? 0u : 1u;
            cluster.IndexOffset += row.IndexOffset;
            rows[i] = cluster;
        }

        string name = GetName(place);
        if (_impostorRows.CanAllocate(1))
            row = UploadImpostor(graph, pools.Impostors, name, made, row);

        GpuClusterModel model = new(row.ClusterOffset, row.ClusterCount, row.ImpostorRow);
        graph.Upload(pools.Models, [model], (ulong)index * GpuClusterModel.Bytes);
        _rows[index] = row;
        Log.Info(
            $"{name}: {made.Clusters.Length} clusters, {made.Indices.Length / 3} triangles in every level, {made.Vertices.Length} vertices above the leaves."
        );
    }

    /// <summary>
    /// The model's impostor atlases asked of the graph and copied up, and its row of the impostor table taken and
    /// written; the row as it was, drawing no impostor, when an atlas was not made.
    /// </summary>
    private Row UploadImpostor(RenderGraph graph, GpuBuffer impostors, string name, ModelClusters made, Row row)
    {
        TextureDescription atlasDescription = GetAtlasDescription($"{name} impostor", GpuFormat.R16G16B16A16Sfloat);
        TextureDescription uvDescription = GetAtlasDescription($"{name} impostor uv", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> atlas = graph.Texture(atlasDescription);
        Result<GpuTexture> uvAtlas = graph.Texture(uvDescription);
        if (Result.FindFailure(atlas, uvAtlas) is { Ok: false } failed)
        {
            Log.Warn($"{atlasDescription.Name} is not drawn: {failed.Error}");

            return row;
        }

        graph.Upload(atlas.Value, MemoryMarshal.AsBytes(made.Impostor.AsSpan()));
        graph.Upload(uvAtlas.Value, MemoryMarshal.AsBytes(made.ImpostorUv.AsSpan()));
        row = row with
        {
            Impostor = atlasDescription,
            ImpostorUv = uvDescription,
            ImpostorRow = (uint)_impostorRows.Allocate(1),
        };
        GpuImpostor impostor = new()
        {
            BoundsMin = made.BoundsMin,
            Atlas = atlas.Value.Id,
            BoundsMax = made.BoundsMax,
            UvAtlas = uvAtlas.Value.Id,
        };
        graph.Upload(impostors, [impostor], (ulong)row.ImpostorRow * GpuImpostor.Bytes);

        return row;
    }

    /// <summary>
    /// Where a row of the model table is in the loading: drawing nothing, as it has no model or its model failed to
    /// load, being read, or in the pools.
    /// </summary>
    private enum RowState : byte
    {
        Empty,
        Loading,
        Ready,
    }

    /// <summary>
    /// A row of the view's model table as the cluster renderer holds it: the model it was made for, and, once ready,
    /// each of its ranges as an offset and a count, and its impostor's atlases and row of the impostor table, 0 for
    /// none.
    /// </summary>
    private readonly record struct Row
    {
        public ModelPlace Place { get; init; }

        public RowState State { get; init; }

        public uint VertexOffset { get; init; }

        public uint VertexCount { get; init; }

        public uint IndexOffset { get; init; }

        public uint IndexCount { get; init; }

        public uint ClusterOffset { get; init; }

        public uint ClusterCount { get; init; }

        public TextureDescription? Impostor { get; init; }

        public TextureDescription? ImpostorUv { get; init; }

        public uint ImpostorRow { get; init; }
    }
}

/// <summary>
/// The cluster renderer's pools and tables this frame: its vertex copies, indices and clusters, each model's clusters
/// and impostor by its model index, the impostors, and every resident impostor's atlases.
/// </summary>
internal readonly record struct ClusterPools(
    GpuBuffer Vertices,
    GpuBuffer Indices,
    GpuBuffer Clusters,
    GpuBuffer Models,
    GpuBuffer Impostors,
    ImmutableArray<GpuResource> Atlases
);
