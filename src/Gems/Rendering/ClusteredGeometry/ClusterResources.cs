using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Files;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Each model on the GPU as the cluster renderer draws it, kept row for row with the view's model table: a model that
/// comes into a row is read from the cache on a worker, as its preprocessor made it, settled into the pools of
/// vertices, indices and clusters, the impostor table and its atlases, and copied up in the frame it arrives; one that
/// leaves its row gives its ranges back to the free lists, and its atlases are no longer asked for, so the GPU provider
/// frees them. A row is empty, and its instances draw nothing, until its model arrives. The pools and the atlases are
/// asked of the graph every frame, so the GPU provider keeps what they hold.
/// </summary>
internal sealed class ClusterResources(
    ClusterPreprocessor clusters,
    Caches caches,
    AssetManager assets,
    Threads threads
)
{
    private const int VertexCapacity = 1 << 21;

    private const int IndexCapacity = 1 << 24;

    private const int ClusterCapacity = 1 << 17;

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

    // The impostor table's rows; row 0 is none, so it is taken from the start.
    private readonly FreeList _impostorRows = CreateImpostorRows();

    private readonly FreeList _vertexRanges = new(VertexCapacity);

    private readonly FreeList _indexRanges = new(IndexCapacity);

    private readonly FreeList _clusterRanges = new(ClusterCapacity);

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
    /// emptied and its model asked for, each model read since the last frame copied up, its leaves drawing from
    /// <paramref name="modelVertices"/>, and every resident impostor's atlases asked for, which are what it returns
    /// beside the pools.
    /// </summary>
    public Result<ClusterPools> Prepare(RenderGraph graph, GpuBuffer modelVertices, ReadOnlySpan<ModelPlace> models)
    {
        Result<GpuBuffer> vertices = graph.Buffer(
            GpuDescriptions.GetBuffer("Cluster vertex pool", (long)VertexCapacity * GpuVertex.Bytes)
        );
        Result<GpuBuffer> indices = graph.Buffer(
            GpuDescriptions.GetBuffer("Cluster index pool", (long)IndexCapacity * sizeof(uint))
        );
        Result<GpuBuffer> pool = graph.Buffer(
            GpuDescriptions.GetBuffer("Cluster pool", (long)ClusterCapacity * GpuCluster.Bytes)
        );
        Result<GpuBuffer> table = graph.Buffer(
            GpuDescriptions.GetBuffer("Cluster model table", (long)models.Length * GpuClusterModel.Bytes)
        );
        Result<GpuBuffer> impostors = graph.Buffer(
            GpuDescriptions.GetBuffer("Impostor table", (long)ImpostorCapacity * GpuImpostor.Bytes)
        );
        foreach (Result made in (ReadOnlySpan<Result>)[vertices, indices, pool, table, impostors])
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        ClusterPools pools = new(vertices.Value, indices.Value, pool.Value, table.Value, impostors.Value, []);

        // A row whose model changed draws nothing until what it holds now arrives.
        while (_rows.Count < models.Length)
        {
            _rows.Add(default);
        }

        for (int index = 0; index < models.Length; index++)
        {
            if (_rows[index].Place == models[index])
                continue;

            Free(_rows[index]);
            ModelPlace place = models[index];
            bool isEmpty = place.Model == Handle<Model>.None;
            _rows[index] = new Row { Place = place, State = isEmpty ? RowState.Empty : RowState.Loading };
            graph.Upload(pools.Models, [default(GpuClusterModel)], (ulong)index * GpuClusterModel.Bytes);
            if (!isEmpty)
                LoadAsync(index, place).FireAndForget();
        }

        foreach ((int index, ModelPlace place, Result<ModelClusters> made) in _madeModels)
        {
            // A model read for a row that has since changed, or that a read before it settled, is dropped; the row asked
            // for what it holds now.
            if (_rows[index].Place != place || _rows[index].State != RowState.Loading)
                continue;

            _rows[index] = Settle(graph, pools, modelVertices, index, made);
        }

        _madeModels.Clear();

        return Result.Success(pools with { Atlases = GetAtlases(graph) });
    }

    private static FreeList CreateImpostorRows()
    {
        FreeList rows = new(ImpostorCapacity);
        rows.Allocate(1);

        return rows;
    }

    /// <summary>
    /// An impostor atlas of <see cref="ModelClusters.ImpostorSize"/> texels a side, in <paramref name="format"/>.
    /// </summary>
    private static TextureDescription GetAtlasDescription(string name, GpuFormat format) =>
        GpuDescriptions.GetTexture(
            name,
            ModelClusters.ImpostorSize,
            ModelClusters.ImpostorSize,
            format,
            TextureUsages.Sampled | TextureUsages.CopyDestination
        );

    /// <summary>
    /// Every resident impostor's atlases, asked of the graph, so the GPU provider keeps them and a pass that samples
    /// them names them in its reads.
    /// </summary>
    private ImmutableArray<GpuResource> GetAtlases(RenderGraph graph)
    {
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

        return atlases.ToImmutable();
    }

    /// <summary>
    /// The row's ranges given back to the free lists; its atlases go once no frame asks for them.
    /// </summary>
    private void Free(in Row row)
    {
        if (row.State != RowState.Ready)
            return;

        _vertexRanges.Free((int)row.VertexOffset, (int)row.VertexCount);
        _indexRanges.Free((int)row.IndexOffset, (int)row.IndexCount);
        _clusterRanges.Free((int)row.ClusterOffset, (int)row.ClusterCount);
        if (row.ImpostorRow != 0)
            _impostorRows.Free((int)row.ImpostorRow, 1);
    }

    /// <summary>
    /// The model's clusters, as its preprocessor cached them as the model loaded, or made here, on a worker, and cached
    /// when the cache could not keep them; settled by the next <see cref="Prepare"/>.
    /// </summary>
    private async Task LoadAsync(int index, ModelPlace place)
    {
        Handle<Model> model = place.Model;
        Result<ModelClusters> made = await caches
            .ReadAsync(model.Id, assets.GetVersion(model), clusters, CancellationToken.None)
            .ConfigureAwait(false);
        if (!made.Ok)
        {
            Result<Model> loaded = await assets.LoadAsync(model, null, CancellationToken.None).ConfigureAwait(false);
            if (loaded.Ok)
            {
                ModelClusters built = await Task.Run(() => clusters.Preprocess(loaded.Value)).ConfigureAwait(false);
                await caches
                    .WriteAsync(model.Id, assets.GetVersion(model), built, clusters, CancellationToken.None)
                    .ConfigureAwait(false);
                made = Result.Success(built);
            }
            else
            {
                made = Result.Failure(loaded.Error);
            }
        }

        await threads.Enter(EngineThread.Render);
        _madeModels.Add((index, place, made));
    }

    /// <summary>
    /// The model's clusters made into the pools' ranges, its impostor given a row of the impostor table and its atlases
    /// when it baked one, all copied up, and its row of the model table written; failed, drawing nothing, when it was not
    /// made or the pools are full.
    /// </summary>
    private Row Settle(
        RenderGraph graph,
        in ClusterPools pools,
        GpuBuffer modelVertices,
        int index,
        Result<ModelClusters> result
    )
    {
        ModelPlace place = _rows[index].Place;
        string name = assets.GetPath(place.Model) ?? $"model {place.Model.Id}";
        if (!result.Ok)
        {
            Log.Error($"{name} draws no clusters: {result.Error}");

            return new Row { Place = place, State = RowState.Failed };
        }

        ModelClusters made = result.Value;
        bool fits =
            _vertexRanges.CanAllocate(made.Vertices.Length)
            && _indexRanges.CanAllocate(made.Indices.Length)
            && _clusterRanges.CanAllocate(made.Clusters.Length);
        if (!fits)
        {
            Log.Error($"{name} draws no clusters: the cluster pools are full.");

            return new Row { Place = place, State = RowState.Failed };
        }

        Row row = new()
        {
            Place = place,
            State = RowState.Ready,
            VertexOffset = (uint)_vertexRanges.Allocate(made.Vertices.Length),
            VertexCount = (uint)made.Vertices.Length,
            IndexOffset = (uint)_indexRanges.Allocate(made.Indices.Length),
            IndexCount = (uint)made.Indices.Length,
            ClusterOffset = (uint)_clusterRanges.Allocate(made.Clusters.Length),
            ClusterCount = (uint)made.Clusters.Length,
        };
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
            cluster.Vertices = isLeaf ? modelVertices.Id : pools.Vertices.Id;
            cluster.IndexOffset += row.IndexOffset;
            rows[i] = cluster;
        }

        if (!made.Impostor.IsEmpty && _impostorRows.CanAllocate(1))
            row = UploadImpostor(graph, pools, name, made, row);

        GpuClusterModel model = new(row.ClusterOffset, row.ClusterCount, row.ImpostorRow);
        graph.Upload(pools.Models, [model], (ulong)index * GpuClusterModel.Bytes);
        Log.Info(
            $"{name}: {made.Clusters.Length} clusters, {made.Indices.Length / 3} triangles in every level, {made.Vertices.Length} vertices above the leaves."
        );

        return row;
    }

    /// <summary>
    /// The model's impostor atlases asked of the graph and copied up, and its row of the impostor table taken and
    /// written; the row as it was, drawing no impostor, when an atlas was not made.
    /// </summary>
    private Row UploadImpostor(RenderGraph graph, in ClusterPools pools, string name, ModelClusters made, Row row)
    {
        TextureDescription atlasDescription = GetAtlasDescription($"{name} impostor", GpuFormat.R16G16B16A16Sfloat);
        TextureDescription uvDescription = GetAtlasDescription($"{name} impostor uv", GpuFormat.R16G16Sfloat);
        Result<GpuTexture> atlas = graph.Texture(atlasDescription);
        Result<GpuTexture> uvAtlas = graph.Texture(uvDescription);
        if (!atlas.Ok || !uvAtlas.Ok)
        {
            Log.Warn($"{atlasDescription.Name} is not drawn: {(atlas.Ok ? uvAtlas.Error : atlas.Error)}");

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
        graph.Upload(pools.Impostors, [impostor], (ulong)row.ImpostorRow * GpuImpostor.Bytes);

        return row;
    }

    /// <summary>
    /// Where a row of the model table is in the loading: no model, being read, in the pools, or drawing nothing.
    /// </summary>
    private enum RowState : byte
    {
        Empty,
        Loading,
        Ready,
        Failed,
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
