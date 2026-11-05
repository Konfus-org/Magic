using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Shaders;

namespace Magic.GIRendering;

/// <summary>
/// A card as the GPU reads it from the card table, 48 bytes: the twin of <c>Scene/Cards.slang</c>'s. One side of a
/// model's box, the model drawn orthographically along that axis into the card atlases at <see cref="OriginX"/> and
/// <see cref="OriginY"/>, <see cref="Width"/> by <see cref="Height"/> texels. A capture owns <see cref="PerCapture"/>
/// cards in a row, one a side.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuCard
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
