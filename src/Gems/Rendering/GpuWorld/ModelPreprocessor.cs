using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// Makes what the GPU holds a model as, its vertices and triangles, as the model loads, and says how the cache keeps it:
/// <see cref="AssetManager"/> keeps it under the model's version, so a model is made once per change to its file.
/// </summary>
internal sealed class ModelPreprocessor : IAssetPreprocessor<Model, MeshDerived>
{
    /// <summary>
    /// Raised when the bytes written change shape, or what they hold is made otherwise.
    /// </summary>
    public int Version => 17;

    public MeshDerived Preprocess(Model asset)
    {
        ImmutableArray<GpuVertex>.Builder vertices = ImmutableArray.CreateBuilder<GpuVertex>();
        ImmutableArray<uint>.Builder indices = ImmutableArray.CreateBuilder<uint>();
        ImmutableArray<GpuMesh>.Builder meshes = ImmutableArray.CreateBuilder<GpuMesh>();
        Vector3 boundsMin = new(float.MaxValue);
        Vector3 boundsMax = new(float.MinValue);
        foreach (Mesh mesh in asset.Meshes)
        {
            uint firstVertex = (uint)vertices.Count;
            meshes.Add(new GpuMesh((uint)indices.Count, (uint)mesh.MaterialSlot));
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                vertices.Add(Packing.EncodeVertex(vertex));
                boundsMin = Vector3.Min(boundsMin, vertex.Position);
                boundsMax = Vector3.Max(boundsMax, vertex.Position);
            }

            foreach (uint index in mesh.Indices.Span)
            {
                indices.Add(firstVertex + index);
            }
        }

        // The sphere's centre is the box's middle, its radius the farthest vertex from there.
        Vector3 center = vertices.Count == 0 ? Vector3.Zero : (boundsMin + boundsMax) * 0.5f;
        float radius2 = 0;
        foreach (GpuVertex vertex in vertices)
        {
            radius2 = Math.Max(radius2, Vector3.DistanceSquared(center, vertex.Position));
        }

        return new MeshDerived
        {
            Vertices = vertices.ToImmutable(),
            Indices = indices.ToImmutable(),
            Meshes = meshes.ToImmutable(),
            BoundsCenter = center,
            BoundsRadius = MathF.Sqrt(radius2),
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
        };
    }

    public void Write(MeshDerived value, IBufferWriter<byte> bytes)
    {
        MemoryMarshal.Write(
            bytes.GetSpan(Unsafe.SizeOf<Header>()),
            new Header(
                value.Vertices.Length,
                value.Indices.Length,
                value.Meshes.Length,
                value.BoundsCenter,
                value.BoundsRadius,
                value.BoundsMin,
                value.BoundsMax
            )
        );
        bytes.Advance(Unsafe.SizeOf<Header>());
        bytes.Write(MemoryMarshal.AsBytes(value.Vertices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Indices.AsSpan()));
        bytes.Write(MemoryMarshal.AsBytes(value.Meshes.AsSpan()));
    }

    public Result<MeshDerived> Read(ReadOnlySpan<byte> bytes)
    {
        if (!MemoryMarshal.TryRead(bytes, out Header header))
            return Result.Failure("the entry is cut short.");

        bytes = bytes[Unsafe.SizeOf<Header>()..];
        long needed =
            ((long)header.VertexCount * GpuVertex.Bytes)
            + ((long)header.IndexCount * sizeof(uint))
            + ((long)header.MeshCount * GpuMesh.Bytes);
        if (header.VertexCount < 0 || header.IndexCount < 0 || header.MeshCount < 0 || bytes.Length < needed)
            return Result.Failure("the entry is cut short.");

        return Result.Success(
            new MeshDerived
            {
                Vertices = Packing.Read<GpuVertex>(ref bytes, header.VertexCount),
                Indices = Packing.Read<uint>(ref bytes, header.IndexCount),
                Meshes = Packing.Read<GpuMesh>(ref bytes, header.MeshCount),
                BoundsCenter = header.BoundsCenter,
                BoundsRadius = header.BoundsRadius,
                BoundsMin = header.BoundsMin,
                BoundsMax = header.BoundsMax,
            }
        );
    }

    /// <summary>
    /// What leads the cached bytes: the counts of what follows and the model's measures, in native byte order like
    /// the rest.
    /// </summary>
    private readonly record struct Header(
        int VertexCount,
        int IndexCount,
        int MeshCount,
        Vector3 BoundsCenter,
        float BoundsRadius,
        Vector3 BoundsMin,
        Vector3 BoundsMax
    );
}
