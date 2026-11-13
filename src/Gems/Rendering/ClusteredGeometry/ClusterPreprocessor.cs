using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.DeferredRendering.World;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Makes what the cluster renderer draws a model as, its cluster hierarchy and impostor, as the model loads.
/// </summary>
/// <remarks>
/// <see cref="AssetManager"/> caches it under the model's version, so a model is clustered once per change to its file.
/// </remarks>
internal sealed class ClusterPreprocessor : IAssetPreprocessor<Model, ModelClusters>
{
    /// <summary>
    /// Raised when the bytes written change shape, or the hierarchy is built otherwise.
    /// </summary>
    public int Version => 3;

    public ModelClusters Preprocess(Model asset) => ClusterBuilder.Build(asset);

    public void Write(ModelClusters value, IBufferWriter<byte> bytes)
    {
        MemoryMarshal.Write(
            bytes.GetSpan(Unsafe.SizeOf<Header>()),
            new Header(
                value.ModelVertexCount,
                value.Vertices.Length,
                value.Indices.Length,
                value.Clusters.Length,
                value.Impostor.Length,
                value.ImpostorUv.Length,
                value.ImpostorViewBounds.Length,
                value.BoundsMin,
                value.BoundsMax
            )
        );
        bytes.Advance(Unsafe.SizeOf<Header>());
        bytes.Write(MemoryMarshal.AsBytes(value.Vertices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Indices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Clusters.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Impostor.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.ImpostorUv.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.ImpostorViewBounds.AsSpan()));
    }

    public Result<ModelClusters> Read(ReadOnlySpan<byte> bytes)
    {
        if (!MemoryMarshal.TryRead(bytes, out Header header))
            return Result.Failure("the entry is cut short.");

        bytes = bytes[Unsafe.SizeOf<Header>()..];
        long needed =
            ((long)header.VertexCount * GpuVertex.Bytes)
            + ((long)header.IndexCount * sizeof(uint))
            + ((long)header.ClusterCount * GpuCluster.Bytes)
            + ((long)header.ImpostorHalves * sizeof(ushort))
            + ((long)header.ImpostorUvHalves * sizeof(ushort))
            + ((long)header.ImpostorViewBoundsCount * sizeof(uint));
        if (
            header.ModelVertexCount < 0
            || header.VertexCount < 0
            || header.IndexCount < 0
            || header.ClusterCount < 0
            || header.ImpostorHalves < 0
            || header.ImpostorUvHalves < 0
            || header.ImpostorViewBoundsCount < 0
            || bytes.Length < needed
        )
            return Result.Failure("the entry is cut short.");

        // Each atlas is copied up whole, so an impostor of another size would leave texels of it unwritten.
        if (
            header.ImpostorHalves != ImpostorBuilder.Size * ImpostorBuilder.Size * 4
            || header.ImpostorUvHalves != ImpostorBuilder.Size * ImpostorBuilder.Size * 2
            || header.ImpostorViewBoundsCount != ImpostorBuilder.Views * ImpostorBuilder.Views
        )
            return Result.Failure("its impostor is not the size of an atlas.");

        ModelClusters clusters = new()
        {
            ModelVertexCount = header.ModelVertexCount,
            Vertices = Packing.Read<GpuVertex>(ref bytes, header.VertexCount),
            Indices = Packing.Read<uint>(ref bytes, header.IndexCount),
            Clusters = Packing.Read<GpuCluster>(ref bytes, header.ClusterCount),
            Impostor = Packing.Read<Half>(ref bytes, header.ImpostorHalves),
            ImpostorUv = Packing.Read<Half>(ref bytes, header.ImpostorUvHalves),
            ImpostorViewBounds = Packing.Read<uint>(ref bytes, header.ImpostorViewBoundsCount),
            BoundsMin = header.BoundsMin,
            BoundsMax = header.BoundsMax,
        };

        // A cluster's triangles are read from the indices as its model settles, and drawn from them.
        foreach (GpuCluster cluster in clusters.Clusters)
        {
            if (cluster.TriangleCount > 0 && cluster.IndexOffset + (cluster.TriangleCount * 3L) > header.IndexCount)
                return Result.Failure("a cluster's triangles are past its indices.");
        }

        return Result.Success(clusters);
    }

    /// <summary>
    /// What leads the cached bytes: the counts of what follows and the model's box, in native byte order like the
    /// rest.
    /// </summary>
    private readonly record struct Header(
        int ModelVertexCount,
        int VertexCount,
        int IndexCount,
        int ClusterCount,
        int ImpostorHalves,
        int ImpostorUvHalves,
        int ImpostorViewBoundsCount,
        Vector3 BoundsMin,
        Vector3 BoundsMax
    );
}
