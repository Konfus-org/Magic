using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// Makes what the renderer draws a model as, its cluster hierarchy and distance field, as the model loads, and says how
/// the cache keeps it: <see cref="AssetManager"/> keeps it under the model's version, so a model is clustered once per
/// change to its file.
/// </summary>
internal sealed class ModelPreprocessor : IAssetPreprocessor<Model, MeshDerived>
{
    /// <summary>
    /// Raised when the bytes written change shape, or the hierarchy is built otherwise.
    /// </summary>
    public int Version => 13;

    public MeshDerived Preprocess(Model asset) => ClusterBuilder.Build(asset);

    public void Write(MeshDerived value, IBufferWriter<byte> bytes)
    {
        MemoryMarshal.Write(
            bytes.GetSpan(Unsafe.SizeOf<Header>()),
            new Header(
                value.Vertices.Length,
                value.Indices.Length,
                value.Clusters.Length,
                value.BoundsCenter,
                value.BoundsRadius,
                value.FieldOrigin,
                value.FieldVoxel,
                value.FieldBand,
                value.Field.Length,
                value.Impostor.Length,
                value.ImpostorUv.Length,
                value.BoundsMin,
                value.BoundsMax
            )
        );
        bytes.Advance(Unsafe.SizeOf<Header>());
        bytes.Write(MemoryMarshal.AsBytes(value.Vertices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Indices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Clusters.AsSpan()));
        bytes.Write(value.Field.AsSpan());
        bytes.Write(MemoryMarshal.AsBytes(value.Impostor.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.ImpostorUv.AsSpan()));
    }

    public Result<MeshDerived> Read(ReadOnlySpan<byte> bytes)
    {
        if (!MemoryMarshal.TryRead(bytes, out Header header))
            return Result.Failure("the entry is cut short.");

        bytes = bytes[Unsafe.SizeOf<Header>()..];
        long needed =
            ((long)header.VertexCount * GpuVertex.Bytes)
            + ((long)header.IndexCount * sizeof(uint))
            + ((long)header.ClusterCount * GpuCluster.Bytes)
            + header.FieldBytes
            + ((long)header.ImpostorHalves * sizeof(ushort))
            + ((long)header.ImpostorUvHalves * sizeof(ushort));
        if (
            header.VertexCount < 0
            || header.IndexCount < 0
            || header.ClusterCount < 0
            || header.FieldBytes < 0
            || header.ImpostorHalves < 0
            || header.ImpostorUvHalves < 0
            || bytes.Length < needed
        )
            return Result.Failure("the entry is cut short.");

        ImmutableArray<GpuVertex> vertices = Take<GpuVertex>(ref bytes, header.VertexCount);
        ImmutableArray<uint> indices = Take<uint>(ref bytes, header.IndexCount);
        ImmutableArray<GpuCluster> clusters = Take<GpuCluster>(ref bytes, header.ClusterCount);
        ImmutableArray<byte> field = Take<byte>(ref bytes, header.FieldBytes);
        ImmutableArray<Half> impostor = Take<Half>(ref bytes, header.ImpostorHalves);
        ImmutableArray<Half> impostorUv = Take<Half>(ref bytes, header.ImpostorUvHalves);

        return Result.Success(
            new MeshDerived
            {
                Vertices = vertices,
                Indices = indices,
                Clusters = clusters,
                BoundsCenter = header.BoundsCenter,
                BoundsRadius = header.BoundsRadius,
                Field = field,
                FieldOrigin = header.FieldOrigin,
                FieldVoxel = header.FieldVoxel,
                FieldBand = header.FieldBand,
                Impostor = impostor,
                ImpostorUv = impostorUv,
                BoundsMin = header.BoundsMin,
                BoundsMax = header.BoundsMax,
            }
        );

        static ImmutableArray<T> Take<T>(ref ReadOnlySpan<byte> bytes, int count)
            where T : struct
        {
            int length = count * Unsafe.SizeOf<T>();
            ImmutableArray<T> items = [.. MemoryMarshal.Cast<byte, T>(bytes[..length])];
            bytes = bytes[length..];

            return items;
        }
    }

    /// <summary>
    /// What leads the cached bytes: the counts of what follows and the model's measures, in native byte order like
    /// the rest.
    /// </summary>
    private readonly record struct Header(
        int VertexCount,
        int IndexCount,
        int ClusterCount,
        Vector3 BoundsCenter,
        float BoundsRadius,
        Vector3 FieldOrigin,
        float FieldVoxel,
        float FieldBand,
        int FieldBytes,
        int ImpostorHalves,
        int ImpostorUvHalves,
        Vector3 BoundsMin,
        Vector3 BoundsMax
    );
}
