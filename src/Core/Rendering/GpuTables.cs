using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Shaders;

namespace Magic.Rendering;

/// <summary>
/// An instance as the GPU reads it from the instance table, 256 bytes: the twin of <c>Scene/Instances.slang</c>'s,
/// in the world; the GPU takes the camera's position off a position once transformed.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuInstance
{
    public const int Bytes = 256;

    /// <summary>
    /// The model's frame to the world, row-major, as <see cref="Matrix4x4"/> is.
    /// </summary>
    public Matrix4x4 World;

    /// <summary>
    /// The world to the model's frame.
    /// </summary>
    public Matrix4x4 InverseWorld;

    /// <summary>
    /// <see cref="World"/> as it was last frame, for motion.
    /// </summary>
    public Matrix4x4 PreviousWorld;

    /// <summary>
    /// The sphere around the instance in the world.
    /// </summary>
    public Vector3 BoundsCenter;

    public float BoundsRadius;

    /// <summary>
    /// The instance's clusters in the cluster pool.
    /// </summary>
    public uint ClusterOffset;

    public uint ClusterCount;

    /// <summary>
    /// The model's field's slot in the atlas plus one; 0 while it has none.
    /// </summary>
    public uint Flags;

    /// <summary>
    /// The card table's capture row, which names the instance's cards; 0 while there is none.
    /// </summary>
    public uint Capture;

    /// <summary>
    /// The material table's row for each of the instance's 8 slots.
    /// </summary>
    public uint Material0;

    public uint Material1;

    public uint Material2;

    public uint Material3;

    public uint Material4;

    public uint Material5;

    public uint Material6;

    public uint Material7;
}

/// <summary>
/// A material as the GPU reads it from the material table, 48 bytes: the twin of <c>Scene/Materials.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuMaterial
{
    public const int Bytes = 48;

    public Vector4 BaseColor;

    public Vector3 Emissive;

    public float Roughness;

    public float Metalness;

    /// <summary>
    /// The heap ids of the base colour texture and the sampler it is read with; 0 for none.
    /// </summary>
    public uint BaseColorTexture;

    public uint BaseColorSampler;

    /// <summary>
    /// Which material contract the row is drawn with: a <see cref="MaterialKind"/>.
    /// </summary>
    public uint Kind;
}

/// <summary>
/// Which material contract a <see cref="GpuMaterial"/> row is drawn with.
/// </summary>
public enum MaterialKind : uint
{
    Standard,

    /// <summary>
    /// The magenta a broken material, shader or texture is drawn as.
    /// </summary>
    Failure,

    /// <summary>
    /// Drawn after the lighting, forward, letting the picture behind through its colour.
    /// </summary>
    Glass,
}

/// <summary>
/// A card as the GPU reads it from the card table, 48 bytes: the twin of <c>Scene/Cards.slang</c>'s. One side of a
/// model's box, the model drawn orthographically along that axis into the card atlases at <see cref="OriginX"/> and
/// <see cref="OriginY"/>, <see cref="Width"/> by <see cref="Height"/> texels. A capture owns <see cref="PerCapture"/>
/// cards in a row, one a side.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuCard
{
    public const int Bytes = 48;

    public const int PerCapture = 6;

    public const int AtlasTexels = 2048;

    public const int MaxTexels = 128;

    public uint OriginX;

    public uint OriginY;

    public uint Width;

    public uint Height;

    /// <summary>
    /// The centre of the model's box, in the model's frame.
    /// </summary>
    public Vector3 Center;

    /// <summary>
    /// Which side: 0 to 5 for +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    public uint Axis;

    /// <summary>
    /// The box's half extents along the model's axes.
    /// </summary>
    public Vector3 Extents;

    public uint Padding;
}
