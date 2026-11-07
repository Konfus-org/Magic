using System.Buffers;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Files;
using Magic.Projects;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.MeshPreprocessor;

/// <summary>
/// Makes what the renderer draws a model as, the cluster hierarchy, and keeps it in the cache under the model's
/// version, so a model is clustered once per change to its file. It is public because nothing in its assembly makes
/// it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class MeshPreprocessorGem(AssetManager assets, Caches caches)
    : IGem,
        IMeshPreprocessor,
        ICache<MeshDerived>
{
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
        int triangles = loaded.Value.Meshes.Sum(mesh => mesh.Indices.Length / 3);
        Log.Info($"{name}: {triangles} triangles clustered into {derived.Clusters.Length} clusters.");
        await caches.WriteAsync(model.Id, version, derived, this, cancel).ConfigureAwait(false);
        progress?.Report(new LoadProgress(work, 2, 2));

        return Result.Success(derived);
    }

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
                value.Field.Length
            )
        );
        bytes.Advance(Unsafe.SizeOf<Header>());
        bytes.Write(MemoryMarshal.AsBytes(value.Vertices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Indices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Clusters.AsSpan()));
        bytes.Write(value.Field.AsSpan());
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
            + header.FieldBytes;
        if (
            header.VertexCount < 0
            || header.IndexCount < 0
            || header.ClusterCount < 0
            || header.FieldBytes < 0
            || bytes.Length < needed
        )
            return Result.Failure("the entry is cut short.");

        ImmutableArray<GpuVertex> vertices = Take<GpuVertex>(ref bytes, header.VertexCount);
        ImmutableArray<uint> indices = Take<uint>(ref bytes, header.IndexCount);
        ImmutableArray<GpuCluster> clusters = Take<GpuCluster>(ref bytes, header.ClusterCount);
        ImmutableArray<byte> field = Take<byte>(ref bytes, header.FieldBytes);

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
        int FieldBytes
    );
}
