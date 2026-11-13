using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Ecs.Components;

namespace Magic.DeferredRendering.World;

/// <summary>
/// An instance as the GPU reads it from the instance table, 248 bytes: the twin of <c>Scene/Instances.slang</c>'s.
/// </summary>
/// <remarks>
/// It is in the world; the GPU takes the camera's position off a position once transformed.
/// </remarks>
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
    /// The model's index, 0 for none: its row in each table that keeps a row a model.
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
/// The shape of a <see cref="GpuLight"/>, the twin of <c>Scene/Lights.slang</c>'s <c>LightType</c>.
/// </summary>
internal enum LightType : uint
{
    Point = 0,
    Spot = 1,
    Rect = 2,
}

/// <summary>
/// A light as the GPU reads it from the light table, 64 bytes: the twin of <c>Scene/Lights.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuLight
{
    public const int Bytes = 64;

    public Vector3 Position;

    public float Range;

    public Vector3 Direction;

    public float Radius;

    public Vector3 Radiance;

    public LightType Type;

    public float CosOuter;

    public float CosInner;

    public float Width;

    public float Height;
}

/// <summary>
/// A mesh as a ray sees it, the twin of <c>Scene/Models.slang</c>'s: where its triangles start in the index pool and
/// its material slot.
/// </summary>
/// <remarks>
/// It is the row of the mesh table a ray hit's geometry names.
/// </remarks>
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
/// A material slot as the GPU reads it from the material table, 64 bytes, the twin of <c>Scene/Materials.slang</c>'s.
/// </summary>
/// <remarks>
/// It holds the shader word, then the struct of the shader that draws the row, its params as the shader's reflection
/// lays them out.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuMaterial
{
    public const int Bytes = 64;

    /// <summary>
    /// The bit of <see cref="Shader"/> set for a translucent shader's row, which draws over the lit picture.
    /// </summary>
    public const uint TranslucentBit = 0x80000000;

    /// <summary>
    /// Where the failure index starts in <see cref="Shader"/>, and its bits from there.
    /// </summary>
    public const int FailureShift = 12;

    public const uint FailureMask = 0x7FFFF;

    /// <summary>
    /// The shader word: the shader that draws the row, the row's failure index, and whether that shader is translucent.
    /// </summary>
    /// <remarks>
    /// Bits 0 to 11 hold the place in <see cref="GpuResources.MaterialShaders"/> of the shader that draws the row. Bits
    /// 12 to 30 hold the row's index in the failure table, 0 where nothing failed. <see cref="TranslucentBit"/> is set
    /// where that shader is translucent.
    /// </remarks>
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

/// <summary>
/// What failed, the twin of <c>Scene/Failures.slang</c>'s <c>FailureKind</c>.
/// </summary>
/// <remarks>
/// The shader draws a part of a material, a param or a texture, in stripes over the real surface, and each other kind
/// solid.
/// </remarks>
internal enum FailureKind : uint
{
    Model = 0,
    Material = 1,
    Shader = 2,
    Param = 3,
    Texture = 4,
}

/// <summary>
/// A row of the failure table, 64 bytes, the twin of the layout <c>Scene/Failures.slang</c> writes.
/// </summary>
/// <remarks>
/// It holds the header, with the kind in bits 0 to 7 and the text's length in bits 16 to 23; the text, a one-byte
/// character code each, 4 in each word, the first in the lowest byte; and a spare word.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuFailure
{
    public const int Bytes = 64;

    public uint Header;

    public GpuFailureText Text;

    public uint Spare;

    /// <summary>
    /// Creates the row of a failure of <paramref name="kind"/>, with the first <see cref="GpuFailureText.Length"/>
    /// characters of <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// A character that is not printable ASCII becomes '?'.
    /// </remarks>
    public static GpuFailure Create(FailureKind kind, string text)
    {
        int length = Math.Min(text.Length, GpuFailureText.Length);
        GpuFailure row = new() { Header = (uint)kind | ((uint)length << 16) };
        for (int i = 0; i < length; i++)
        {
            char c = text[i];
            uint code = c is >= ' ' and <= '~' ? c : '?';
            row.Text[i / 4] |= code << (i % 4 * 8);
        }

        return row;
    }
}

/// <summary>
/// A <see cref="GpuFailure"/>'s text, inline: <see cref="Length"/> one-byte character codes, 4 in each word.
/// </summary>
[InlineArray(14)]
internal struct GpuFailureText
{
    /// <summary>
    /// The most characters the text holds.
    /// </summary>
    public const int Length = 56;

    private uint _first;
}
