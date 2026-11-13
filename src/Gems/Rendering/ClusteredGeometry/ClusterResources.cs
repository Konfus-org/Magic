using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Holds each model on the GPU as the cluster renderer draws it, row for row with the view's model table.
/// </summary>
/// <remarks>
/// A model that comes into a row is read from the cache on a worker, settled into the pools of vertices, indices and
/// clusters, the impostor table and its atlases, and copied up in the frame it arrives. The pools grow to hold it. A
/// model that leaves its row gives its ranges back to the free lists, and the GPU provider frees its atlases once no
/// frame asks for them. A row is empty, and its instances draw nothing, until its model arrives. The pools and the
/// atlases are asked of the graph every frame, so the GPU provider keeps what they hold.
/// </remarks>
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
    /// The models of <see cref="_madeModels"/> that took their ranges, by the row and place they took them for, to copy
    /// up once the pools are asked for at their size.
    /// </summary>
    /// <remarks>
    /// One whose copies were not staged waits for the next frame.
    /// </remarks>
    private readonly List<(int Index, ModelPlace Place, ModelClusters Made)> _placedModels = [];

    // The impostor table's rows; row 0 is none, so it is taken from the start.
    private readonly FreeList _impostorRows = new(ImpostorCapacity, 1);

    // About 3 MiB in all at the start; each grows with the scene.
    private readonly BufferPool _vertices = new(new("Cluster vertex pool", 0), GpuVertex.Bytes, 1 << 16);

    private readonly BufferPool _indices = new(new("Cluster index pool", 0), sizeof(uint), 1 << 18);

    private readonly BufferPool _clusters = new(new("Cluster pool", 0), GpuCluster.Bytes, 1 << 12);

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
    /// Asks for the pools and tables, and keeps them row for row with <paramref name="models"/>.
    /// </summary>
    /// <remarks>
    /// Each row whose model changed is emptied and its model asked for. Each model read since the last frame is copied
    /// up, and the pools grow where it does not fit.
    /// </remarks>
    /// <returns>The pools and tables, and every resident impostor's atlases.</returns>
    public Result<ClusterPools> Prepare(RenderGraph graph, ReadOnlySpan<ModelPlace> models)
    {
        // A row past the view's last row in use has lost its model: its ranges given back, and the row dropped.
        for (int index = models.Length; index < _rows.Count; index++)
        {
            Free(_rows[index]);
        }

        if (_rows.Count > models.Length)
            _rows.RemoveRange(models.Length, _rows.Count - models.Length);

        while (_rows.Count < models.Length)
        {
            _rows.Add(default);
        }

        // The GPU table is as long as the row list's capacity, which doubles as rows come and never shrinks, so it is
        // not made again each time a model arrives or leaves.
        Result<GpuBuffer> table = graph.Buffer(
            new("Cluster model table", (long)_rows.Capacity * GpuClusterModel.Bytes)
        );
        Result<GpuBuffer> impostors = graph.Buffer(
            new("Impostor table", (long)ImpostorCapacity * GpuImpostor.RowBytes)
        );
        if (Result.FindFailure(table, impostors) is { Ok: false } failedTable)
            return Result.Failure(failedTable.Error);

        // A row whose model changed draws nothing until what it holds now arrives.
        for (int index = 0; index < models.Length; index++)
        {
            if (_rows[index].Place == models[index])
                continue;

            ModelPlace place = models[index];
            bool isEmpty = place.Model == Handle<Model>.None;

            // A row not emptied keeps its old model, whose ranges are not freed; the next frame tries again.
            if (!graph.Upload(table.Value, [default(GpuClusterModel)], (ulong)index * GpuClusterModel.Bytes))
                return Result.Failure("a row of the model table was not emptied.");

            Free(_rows[index]);
            _rows[index] = new Row { Place = place, State = isEmpty ? RowState.Empty : RowState.Loading };
            if (!isEmpty)
                LoadAsync(index, place).FireAndForget();
        }

        // Each model read since the last frame takes its ranges, which may grow the pools. A model read for a row that
        // has since changed or been dropped, or that a read before it settled, is dropped; the row asked for what it
        // holds now.
        foreach ((int index, ModelPlace place, Result<ModelClusters> made) in _madeModels)
        {
            if (index >= _rows.Count || _rows[index].Place != place || _rows[index].State != RowState.Loading)
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
            _placedModels.Add((index, place, model));
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
        // One whose row has since changed is dropped; one not staged stays for the next frame.
        for (int i = _placedModels.Count - 1; i >= 0; i--)
        {
            (int index, ModelPlace place, ModelClusters made) = _placedModels[i];
            bool isStale = _rows.Count <= index || _rows[index].Place != place || _rows[index].State != RowState.Ready;
            if (isStale || Upload(graph, pools, index, made))
                _placedModels.RemoveAt(i);
        }

        // Every resident impostor's atlases, asked of the graph, so the GPU provider keeps them and a pass that samples
        // them names them in its reads.
        ImmutableArray<GpuResource>.Builder atlases = ImmutableArray.CreateBuilder<GpuResource>();
        foreach (Row row in _rows)
        {
            if (row.ImpostorDescription is not { } atlas || row.ImpostorUvDescription is not { } uvAtlas)
                continue;

            if (graph.Texture(atlas) is { Ok: true } surface)
                atlases.Add(surface.Value);

            if (graph.Texture(uvAtlas) is { Ok: true } uvs)
                atlases.Add(uvs.Value);
        }

        return Result.Success(pools with { Atlases = atlases.ToImmutable() });
    }

    /// <summary>
    /// Describes an impostor atlas of <see cref="ImpostorBuilder.Size"/> texels a side, in <paramref name="format"/>.
    /// </summary>
    private static TextureDescription GetAtlasDescription(string name, GpuFormat format) =>
        new(
            name,
            ImpostorBuilder.Size,
            ImpostorBuilder.Size,
            format,
            TextureUsages.Sampled | TextureUsages.CopyDestination
        );

    /// <summary>
    /// Gives the row's ranges back to the free lists, when it holds a model; its atlases go once no frame asks for
    /// them.
    /// </summary>
    private void Free(Row row)
    {
        if (row.State != RowState.Ready)
            return;

        _vertices.Free((int)row.VertexOffset, (int)row.VertexCount);
        _indices.Free((int)row.IndexOffset, (int)row.IndexCount);
        _clusters.Free((int)row.ClusterOffset, (int)row.ClusterCount);
        if (row.ImpostorRow != 0)
            _impostorRows.Free((int)row.ImpostorRow, 1);
    }

    private string GetName(ModelPlace place) => assets.GetPath(place.Model) ?? $"model {place.Model.Id}";

    /// <summary>
    /// Reads the model's clusters, as its preprocessor made them, on a worker, for the next <see cref="Prepare"/> to
    /// settle.
    /// </summary>
    /// <remarks>
    /// The failure cube's clusters are made from memory.
    /// </remarks>
    private async Task LoadAsync(int index, ModelPlace place)
    {
        Result<ModelClusters> made;
        try
        {
            made =
                place.Model == FailureCube.Handle
                    ? Result.Success(await Task.Run(() => clusters.Preprocess(FailureCube.Model)).ConfigureAwait(false))
                    : await assets
                        .LoadPreprocessedAsync(place.Model, clusters, CancellationToken.None)
                        .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            made = Result.Failure($"making them threw: {ex}");
        }

        await threads.Enter(EngineThread.Render);
        _madeModels.Add((index, place, made));
    }

    /// <summary>
    /// Copies the model's clusters into the ranges that its row took in <paramref name="pools"/>, and writes its row of
    /// the model table last.
    /// </summary>
    /// <remarks>
    /// When the model baked an impostor, the impostor gets a row of the impostor table and its atlases.
    /// </remarks>
    /// <returns>False, with no row of the model table written, when a copy was not staged.</returns>
    private bool Upload(RenderGraph graph, ClusterPools pools, int index, ModelClusters made)
    {
        Row row = _rows[index];
        ModelPlace place = row.Place;
        if (!graph.Upload(pools.Vertices, made.Vertices.AsSpan(), (ulong)row.VertexOffset * GpuVertex.Bytes))
            return false;

        // An index under the model's own vertex count is into the world's vertex pool, where its model's vertices start
        // at its place; the rest are into this pool's copies.
        uint modelVertexCount = (uint)made.ModelVertexCount;
        Span<uint> rebased = graph.Upload<uint>(
            pools.Indices,
            made.Indices.Length,
            (ulong)row.IndexOffset * sizeof(uint)
        );
        if (rebased.Length < made.Indices.Length)
            return false;

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
        if (rows.Length < made.Clusters.Length)
            return false;

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
        {
            if (UploadImpostor(graph, pools.Impostors, name, made, row) is not { } withImpostor)
                return false;

            row = withImpostor;
        }

        GpuClusterModel model = new(row.ClusterOffset, row.ClusterCount, row.ImpostorRow);
        if (!graph.Upload(pools.Models, [model], (ulong)index * GpuClusterModel.Bytes))
        {
            if (row.ImpostorRow != 0)
                _impostorRows.Free((int)row.ImpostorRow, 1);

            return false;
        }

        _rows[index] = row;
        Log.Info(
            $"{name}: {made.Clusters.Length} clusters, {made.Indices.Length / 3} triangles in every level, {made.Vertices.Length} vertices above the leaves."
        );

        return true;
    }

    /// <summary>
    /// Asks for the model's impostor atlases and copies them up, then takes and writes its row of the impostor table.
    /// </summary>
    /// <returns>
    /// The row; the row as it was, drawing no impostor, when an atlas was not made; or null, with no row taken, when a
    /// copy was not staged.
    /// </returns>
    private Row? UploadImpostor(RenderGraph graph, GpuBuffer impostors, string name, ModelClusters made, Row row)
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

        if (
            !graph.Upload(atlas.Value, MemoryMarshal.AsBytes(made.Impostor.AsSpan()))
            || !graph.Upload(uvAtlas.Value, MemoryMarshal.AsBytes(made.ImpostorUv.AsSpan()))
        )
            return null;

        row = row with
        {
            ImpostorDescription = atlasDescription,
            ImpostorUvDescription = uvDescription,
            ImpostorRow = (uint)_impostorRows.Allocate(1),
        };
        GpuImpostor impostor = new()
        {
            BoundsMin = made.BoundsMin,
            Atlas = atlas.Value.Id,
            BoundsMax = made.BoundsMax,
            UvAtlas = uvAtlas.Value.Id,
        };
        ulong rowOffset = (ulong)row.ImpostorRow * GpuImpostor.RowBytes;
        if (
            graph.Upload(impostors, [impostor], rowOffset)
            && graph.Upload(impostors, made.ImpostorViewBounds.AsSpan(), rowOffset + GpuImpostor.Bytes)
        )
            return row;

        _impostorRows.Free((int)row.ImpostorRow, 1);

        return null;
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
    /// A row of the view's model table as the cluster renderer holds it.
    /// </summary>
    /// <remarks>
    /// It holds the model it was made for and, once ready, each of its ranges as an offset and a count, its impostor's
    /// atlases, and its row of the impostor table, 0 for none.
    /// </remarks>
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

        public TextureDescription? ImpostorDescription { get; init; }

        public TextureDescription? ImpostorUvDescription { get; init; }

        public uint ImpostorRow { get; init; }
    }
}

/// <summary>
/// The cluster renderer's pools and tables this frame, one set for every view.
/// </summary>
/// <remarks>
/// They hold its vertex copies, indices and clusters, each model's clusters and impostor by its model index, the
/// impostors, and every resident impostor's atlases. The renderer makes them once a frame for the step that draws each
/// view.
/// </remarks>
public readonly record struct ClusterPools(
    GpuBuffer Vertices,
    GpuBuffer Indices,
    GpuBuffer Clusters,
    GpuBuffer Models,
    GpuBuffer Impostors,
    ImmutableArray<GpuResource> Atlases
);
