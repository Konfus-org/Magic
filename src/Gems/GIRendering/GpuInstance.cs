using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Ecs.Components;

namespace Magic.GIRendering;

/// <summary>
/// An instance as the GPU reads it from the instance table, 256 bytes: the twin of <c>Scene/Instances.slang</c>'s,
/// in the world; the GPU takes the camera's position off a position once transformed.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuInstance
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
    /// <see cref="World"/> before it last changed, for motion in the frame <see cref="MovedFrame"/>.
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
    /// The frame <see cref="World"/> last changed in: in any other, the instance stood still.
    /// </summary>
    public uint MovedFrame;

    public uint Padding1;

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
internal struct GpuMaterialRows
{
    private uint _first;
}
