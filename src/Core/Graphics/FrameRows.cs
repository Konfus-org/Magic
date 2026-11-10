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
    public const int Bytes = 528;

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
}

/// <summary>
/// The frame's models, the twin of the library's <c>Scene/Models.slang</c>: the instance table and its count, the
/// pools and tables the instances are drawn from, and the mesh table rays find triangles through, written once a frame
/// into the frame's buffer after <see cref="FrameConstants"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuModels
{
    public const int Offset = FrameConstants.Bytes;

    public const int Bytes = 24;

    public uint Instances;

    public uint Count;

    public uint Vertices;

    public uint Indices;

    public uint Materials;

    public uint Meshes;
}

/// <summary>
/// The world as rays see it, the twin of the library's <c>Scene/Models.slang</c> TracedWorld: the acceleration structure
/// over the instances and the hit materials, written once a frame into the frame's buffer after <see cref="GpuModels"/>
/// by what builds the structure, and all 0 where nothing does.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuTracedWorld
{
    public const int Offset = GpuModels.Offset + GpuModels.Bytes;

    public const int Bytes = 8;

    public uint Acceleration;

    public uint HitMaterials;
}

/// <summary>
/// The frame's lights, the twin of the library's <c>Scene/Lights.slang</c>: the sun, the sky's tables and the light
/// table, written once a frame into the frame's buffer after <see cref="GpuTracedWorld"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuLights
{
    public const int Offset = GpuTracedWorld.Offset + GpuTracedWorld.Bytes;

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
/// The frame's shadows, the twin of the library's <c>Scene/Shadows.slang</c>: the mask, the sun's tiles and its tint,
/// written once a frame into the frame's buffer after <see cref="GpuLights"/> by what traces them, and all 0 where nothing
/// does.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuShadows
{
    public const int Offset = GpuLights.Offset + GpuLights.Bytes;

    public const int Bytes = 12;

    public uint Mask;

    public uint SunTiles;

    public uint SunTint;
}

/// <summary>
/// The light that reaches the view's surfaces indirectly, the twin of the library's <c>Light/Indirect.slang</c>
/// IndirectLight: the traced diffuse light and reflections and the probe grid around them, written once a frame into the
/// frame's buffer after <see cref="GpuShadows"/> by what traces them, and all 0 where nothing does.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuIndirectLight
{
    public const int Offset = GpuShadows.Offset + GpuShadows.Bytes;

    public const int Bytes = 84;

    public uint Diffuse;

    public uint Reflections;

    public ProbeGridConstants Grid;
}

/// <summary>
/// The probe grid: its size, the heap ids of its buffers and where it lies in the world, the twin of the library's
/// <c>Light/Probes.slang</c> ProbeGridConstants.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct ProbeGridConstants
{
    /// <summary>
    /// Probes along x; with the two after, the twin of <c>dimensions</c>.
    /// </summary>
    public uint DimensionX;

    public uint DimensionY;

    public uint DimensionZ;

    public uint Probes;

    public uint Depth;

    public uint DepthSampler;

    public uint Rays;

    public uint RayList;

    public uint RayArguments;

    public uint RayCounts;

    public uint Variance;

    /// <summary>
    /// 0 while the probes hold nothing: this frame's update starts them afresh.
    /// </summary>
    public uint HasHistory;

    /// <summary>
    /// Where the first probe rests in the world, how far a probe's depth reaches, and the metres between neighbouring
    /// probes.
    /// </summary>
    public Vector3 Origin;

    public float MaxDistance;

    public Vector3 CellSize;
}
