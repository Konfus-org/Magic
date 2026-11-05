using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Caching;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Projects;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.MeshPreprocessor;

/// <summary>
/// Makes what the renderer draws a model as, the cluster hierarchy, and keeps it in the cache under the model's
/// version, so a model is clustered once per change to its file. It is public because nothing in its assembly makes
/// it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class MeshPreprocessorGem(Assets assets, Caches caches) : IGem, IMeshPreprocessor, ICache<MeshDerived>
{
    private const int HeaderBytes = 52;

    /// <summary>
    /// Raised when the bytes written change shape, or the hierarchy is built otherwise.
    /// </summary>
    public int Version => 10;

    public async Task<Result<MeshDerived>> GetAsync(
        Handle<Model> model,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string name = assets.GetPath(model) ?? $"model {model.Id}";
        string work = $"Clustering {name}";
        progress?.Report(new LoadProgress(work, 0, 2));
        Result<Model> loaded = await assets.LoadAsync(model, progress, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
            return Result.Failure($"{name} could not be read: {loaded.Error}");

        ulong version = assets.GetVersion(model);
        Result<MeshDerived> cached = await caches.ReadAsync(model.Id, version, this, cancel).ConfigureAwait(false);
        if (cached.Ok)
        {
            progress?.Report(new LoadProgress(work, 2, 2));

            return cached;
        }

        progress?.Report(new LoadProgress(work, 1, 2));
        MeshDerived derived = await Task.Run(() => ClusterBuilder.Build(loaded.Value), cancel).ConfigureAwait(false);
        int triangles = 0;
        foreach (Mesh mesh in loaded.Value.Meshes)
        {
            triangles += mesh.Indices.Length / 3;
        }

        Log.Info($"{name}: {triangles} triangles clustered into {derived.Clusters.Length} clusters.");
        await caches.WriteAsync(model.Id, version, derived, this, cancel).ConfigureAwait(false);
        progress?.Report(new LoadProgress(work, 2, 2));

        return Result.Success(derived);
    }

    public void Write(MeshDerived value, IBufferWriter<byte> bytes)
    {
        Span<byte> header = bytes.GetSpan(HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header, value.Vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], value.Indices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], value.Clusters.Length);
        BinaryPrimitives.WriteSingleLittleEndian(header[12..], value.BoundsCenter.X);
        BinaryPrimitives.WriteSingleLittleEndian(header[16..], value.BoundsCenter.Y);
        BinaryPrimitives.WriteSingleLittleEndian(header[20..], value.BoundsCenter.Z);
        BinaryPrimitives.WriteSingleLittleEndian(header[24..], value.BoundsRadius);
        BinaryPrimitives.WriteSingleLittleEndian(header[28..], value.FieldOrigin.X);
        BinaryPrimitives.WriteSingleLittleEndian(header[32..], value.FieldOrigin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(header[36..], value.FieldOrigin.Z);
        BinaryPrimitives.WriteSingleLittleEndian(header[40..], value.FieldVoxel);
        BinaryPrimitives.WriteSingleLittleEndian(header[44..], value.FieldBand);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..], value.Field.Length);
        bytes.Advance(HeaderBytes);
        bytes.Write(MemoryMarshal.AsBytes(value.Vertices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Indices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Clusters.AsSpan()));
        bytes.Write(value.Field.AsSpan());
    }

    public Result<MeshDerived> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
            return Result.Failure("the entry is cut short.");

        int vertexCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int indexCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int clusterCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        Vector3 center = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[20..])
        );
        float radius = BinaryPrimitives.ReadSingleLittleEndian(bytes[24..]);
        Vector3 fieldOrigin = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[28..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[32..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[36..])
        );
        float fieldVoxel = BinaryPrimitives.ReadSingleLittleEndian(bytes[40..]);
        float fieldBand = BinaryPrimitives.ReadSingleLittleEndian(bytes[44..]);
        int fieldBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        bytes = bytes[HeaderBytes..];
        long needed =
            ((long)vertexCount * GpuVertex.Bytes)
            + ((long)indexCount * sizeof(uint))
            + ((long)clusterCount * GpuCluster.Bytes)
            + fieldBytes;
        if (vertexCount < 0 || indexCount < 0 || clusterCount < 0 || fieldBytes < 0 || bytes.Length < needed)
            return Result.Failure("the entry is cut short.");

        ImmutableArray<GpuVertex> vertices =
        [
            .. MemoryMarshal.Cast<byte, GpuVertex>(bytes[..(vertexCount * GpuVertex.Bytes)]),
        ];
        bytes = bytes[(vertexCount * GpuVertex.Bytes)..];
        ImmutableArray<uint> indices = [.. MemoryMarshal.Cast<byte, uint>(bytes[..(indexCount * sizeof(uint))])];
        bytes = bytes[(indexCount * sizeof(uint))..];
        ImmutableArray<GpuCluster> clusters =
        [
            .. MemoryMarshal.Cast<byte, GpuCluster>(bytes[..(clusterCount * GpuCluster.Bytes)]),
        ];

        bytes = bytes[(clusterCount * GpuCluster.Bytes)..];
        ImmutableArray<byte> field = [.. bytes[..fieldBytes]];

        return Result.Success(
            new MeshDerived
            {
                Vertices = vertices,
                Indices = indices,
                Clusters = clusters,
                BoundsCenter = center,
                BoundsRadius = radius,
                Field = field,
                FieldOrigin = fieldOrigin,
                FieldVoxel = fieldVoxel,
                FieldBand = fieldBand,
            }
        );
    }
}
