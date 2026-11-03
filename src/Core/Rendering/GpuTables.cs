using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Shaders;

namespace Magic.Rendering;

/// <summary>
/// An instance as the GPU reads it from the instance table, 192 bytes: the twin of <c>Scene/Instances.slang</c>'s.
/// Positions are relative to the camera, the camera's own translation taken off on the CPU in double precision.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuInstance
{
    public const int Bytes = 192;

    /// <summary>
    /// The model's frame to the camera-relative world, row-major, as <see cref="Matrix4x4"/> is.
    /// </summary>
    public Matrix4x4 World;

    /// <summary>
    /// <see cref="World"/> as it was last frame, for motion.
    /// </summary>
    public Matrix4x4 PreviousWorld;

    /// <summary>
    /// The sphere around the instance in the camera-relative world.
    /// </summary>
    public Vector3 BoundsCenter;

    public float BoundsRadius;

    /// <summary>
    /// The instance's clusters in the cluster pool.
    /// </summary>
    public uint ClusterOffset;

    public uint ClusterCount;

    public uint Flags;

    public uint Padding;

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
    /// The heap id of the base colour texture; 0 for none.
    /// </summary>
    public uint BaseColorTexture;

    public uint Flags;

    public uint Padding;
}
