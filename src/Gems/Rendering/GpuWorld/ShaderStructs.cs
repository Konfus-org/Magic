using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Ecs.Components;

namespace Magic.GpuWorld;

/// <summary>
/// An instance as the GPU reads it from the instance table, 256 bytes: the twin of <c>Scene/Instances.slang</c>'s,
/// in the world; the GPU takes the camera's position off a position once transformed.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuInstance
{
    public const int Bytes = 248;

    /// <summary>
    /// The model's frame to the world, row-major, as <see cref="Matrix4x4"/> is.
    /// </summary>
    public Matrix4x4 World;

    /// <summary>
    /// The world to the model's frame.
    /// </summary>
    public Matrix4x4 InverseWorld;

    /// <summary>
    /// <see cref="World"/> before it last changed, for motion in the frame <see cref="MovedFrame"/>.
    /// </summary>
    public Matrix4x4 PreviousWorld;

    /// <summary>
    /// The sphere around the instance in the world.
    /// </summary>
    public Vector3 BoundsCenter;

    public float BoundsRadius;

    /// <summary>
    /// The model's index, 0 for none: its row of any table a renderer keeps a row a model in.
    /// </summary>
    public uint Model;

    /// <summary>
    /// The frame <see cref="World"/> last changed in: in any other, the instance stood still.
    /// </summary>
    public uint MovedFrame;

    /// <summary>
    /// The material table's row for each of the instance's slots.
    /// </summary>
    public GpuMaterialRows Materials;
}

/// <summary>
/// A material table row for each of a renderer's <see cref="MaterialSlots"/>, inline so the instance stays one block of
/// bytes.
/// </summary>
[InlineArray(MaterialSlots.Capacity)]
public struct GpuMaterialRows : IEquatable<GpuMaterialRows>
{
    private uint _first;

    public static bool operator ==(GpuMaterialRows left, GpuMaterialRows right) => left.Equals(right);

    public static bool operator !=(GpuMaterialRows left, GpuMaterialRows right) => !left.Equals(right);

    public readonly bool Equals(GpuMaterialRows other) => ((ReadOnlySpan<uint>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is GpuMaterialRows other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(MemoryMarshal.AsBytes((ReadOnlySpan<uint>)this));

        return hash.ToHashCode();
    }
}

/// <summary>
/// A light as the GPU reads it from the light table, 64 bytes: the twin of <c>Scene/Lights.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuLight
{
    public const int Bytes = 64;

    public const uint Point = 0;

    public const uint Spot = 1;

    public const uint Rect = 2;

    public Vector3 Position;

    public float Range;

    public Vector3 Direction;

    public float Radius;

    public Vector3 Radiance;

    public uint Type;

    public float CosOuter;

    public float CosInner;

    public float Width;

    public float Height;
}

/// <summary>
/// A mesh as a ray sees it, the twin of <c>Scene/Models.slang</c>'s: where its triangles start in the index pool and
/// its material slot, the row of the mesh table an acceleration structure's geometry names.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuMesh(uint IndexOffset, uint MaterialSlot)
{
    public const int Bytes = 8;
}

/// <summary>
/// A vertex as the GPU reads it from the vertex pool, 32 bytes: the twin of <c>Scene/Vertices.slang</c>'s. Normals and
/// tangents are octahedral, packed as <c>Core/Pack.slang</c> packs them.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuVertex
{
    public const int Bytes = 32;

    public Vector3 Position;

    /// <summary>
    /// The unit normal, octahedral at 16 bits an axis.
    /// </summary>
    public uint Normal;

    /// <summary>
    /// The unit tangent, octahedral at 16 bits an axis.
    /// </summary>
    public uint Tangent;

    /// <summary>
    /// 1 or -1: what turns the cross of the normal and the tangent into the bitangent.
    /// </summary>
    public float TangentSign;

    public Vector2 Uv;
}

/// <summary>
/// A material slot as the GPU reads it from the material table, 64 bytes, the twin of <c>Scene/Materials.slang</c>'s:
/// which of <see cref="GpuResources.MaterialShaders"/> draws it, with <see cref="TranslucentBit"/> where that shader is
/// translucent, then that shader's struct, its params as the shader's reflection lays them out.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuMaterial
{
    public const int Bytes = 64;

    /// <summary>
    /// The bit of <see cref="Shader"/> set for a translucent shader's row, which draws over the lit picture.
    /// </summary>
    public const uint TranslucentBit = 0x80000000;

    public uint Shader;

    public GpuMaterialParams Params;
}

/// <summary>
/// A <see cref="GpuMaterial"/>'s params, inline: the bytes the material shader's struct reads.
/// </summary>
[InlineArray((GpuMaterial.Bytes / 4) - 1)]
internal struct GpuMaterialParams
{
    private uint _first;
}
