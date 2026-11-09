using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Graphics;

/// <summary>
/// What every pass of a frame reads, the twin of the library's <c>Core/Frame.slang</c>: the view, the output's size and
/// the heap ids of the view's textures, written once a frame at the start of the frame's buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct FrameConstants
{
    public const int Bytes = 532;

    public Matrix4x4 View;

    public Matrix4x4 Projection;

    public Matrix4x4 ViewProjection;

    public Matrix4x4 InverseViewProjection;

    public Vector4 FrustumPlane0;

    public Vector4 FrustumPlane1;

    public Vector4 FrustumPlane2;

    public Vector4 FrustumPlane3;

    public Vector4 FrustumPlane4;

    public Vector4 FrustumPlane5;

    public Vector4 CameraPosition;

    public Matrix4x4 PreviousViewProjection;

    public Vector4 PreviousCameraPosition;

    public uint Width;

    public uint Height;

    public float Near;

    public float PixelsPerRadian;

    public uint FrameIndex;

    public uint Depth;

    public uint GBufferNormal;

    public uint GBufferBase;

    public uint GBufferSurface;

    public uint Output;

    public Vector2 Jitter;

    public uint Velocity;

    public uint Hdr;

    public float TimeSec;

    public float DeltaSec;

    public uint GlassDepth;

    public uint GlassBase;

    public uint GlassNormal;

    public uint GlassSurface;

    /// <summary>
    /// The library's <c>Light/Indirect.slang</c> IndirectLight, written for a frame by whatever traces it.
    /// </summary>
    public uint IndirectLight;
}

/// <summary>
/// The frame's models, the twin of the library's <c>Scene/Models.slang</c>: the instance table and its count, the
/// pools and tables the instances are drawn from, and on a GPU that traces rays the world's acceleration structure, the
/// hit materials, the mesh table and the box over what rays see, written once a frame into the frame's buffer after <see cref="FrameConstants"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuModels
{
    public const int Offset = FrameConstants.Bytes;

    public const int Bytes = 56;

    public uint Instances;

    public uint Count;

    public uint Vertices;

    public uint Indices;

    public uint Materials;

    public uint Acceleration;

    public uint HitMaterials;

    public uint Meshes;

    /// <summary>
    /// The box over the world boxes of the instances rays see; min over max where none do.
    /// </summary>
    public Vector3 BoxMin;

    public Vector3 BoxMax;
}

/// <summary>
/// The frame's lights, the twin of the library's <c>Scene/Lights.slang</c>: the sun, the sky's tables and the light
/// table, written once a frame into the frame's buffer after <see cref="GpuModels"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuLights
{
    public const int Offset = GpuModels.Offset + GpuModels.Bytes;

    public const int Bytes = 84;

    public Vector4 SunDirection;

    public Vector4 SunIlluminance;

    public Vector4 SkyRadiance;

    public float CameraAltitude;

    public uint TransmittanceLut;

    public uint MultiScatteringLut;

    public uint SkyViewLut;

    public uint LutSampler;

    public uint HasSky;

    public uint Table;

    public uint Count;

    public uint Tiles;
}

/// <summary>
/// The frame's shadows, the twin of the library's <c>Scene/Shadows.slang</c>: the masks, the sun's tiles and its tint,
/// each 0 on a GPU that traces no rays, written once a frame into the frame's buffer after <see cref="GpuLights"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuShadows
{
    public const int Offset = GpuLights.Offset + GpuLights.Bytes;

    public const int Bytes = 16;

    public uint Mask;

    public uint Traced;

    public uint SunTiles;

    public uint SunTint;
}
