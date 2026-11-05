using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Shaders;

namespace Magic.GIRendering;

/// <summary>
/// What the sun's shadow passes read, the twin of <c>Shadow/Virtual.slang</c>'s: the light's frame, the clipmap's
/// sizes, the heap ids of its tables and each level's window.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowConstants
{
    public const int Bytes = 528;

    public const int MaxLevels = 24;

    public Vector4 LightX;

    public Vector4 LightY;

    public Vector4 LightZ;

    public Vector4 CameraLight;

    public uint Levels;

    public uint TableWidth;

    public uint PageTexels;

    public uint PoolPagesPerRow;

    public float BaseTexel;

    public float FootprintPerMetre;

    public uint MaxDraws;

    public uint MaxInvalidPages;

    public uint PageTable;

    public uint PageRequests;

    public uint FreeList;

    public uint InvalidPages;

    public uint Draws;

    public uint DrawArguments;

    public uint Counts;

    public uint Pool;

    public uint DirtyInstances;

    public uint DirtyCount;

    public uint ClearArguments;

    public uint Padding;

    public ShadowWindows Windows;
}

/// <summary>
/// A level's window: the page, counted on the light's plane, at its corner.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowWindow(int X, int Y, int Z, int W);

/// <summary>
/// Every level's window, <see cref="ShadowConstants.MaxLevels"/> of them inline.
/// </summary>
[InlineArray(ShadowConstants.MaxLevels)]
internal struct ShadowWindows
{
    private ShadowWindow _first;
}

/// <summary>
/// An instance that changed this frame, the twin of <c>Shadow/Virtual.slang</c>'s: the spheres it had and has in
/// the world, each of no radius when it had none, so the pages under both are drawn again.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct DirtyInstance
{
    public const int Bytes = 32;

    public Vector3 OldCenter;

    public float OldRadius;

    public Vector3 NewCenter;

    public float NewRadius;
}
