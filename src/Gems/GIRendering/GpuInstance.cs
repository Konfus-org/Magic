using System.Numerics;
using System.Runtime.InteropServices;

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
