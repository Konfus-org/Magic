using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.DeferredRendering.View;

/// <summary>
/// What every pass of a frame reads: the view, the output's size and the heap ids of the view's textures.
/// </summary>
/// <remarks>
/// The twin of the library's <c>Core/Frame.slang</c>, written once a frame at the start of the frame's buffer.
/// </remarks>
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
/// The frame's models: the instance table and its count, and the pools and tables the instances are drawn from.
/// </summary>
/// <remarks>
/// It also holds the mesh table rays find triangles through, and the failure table the material rows name. The twin of
/// <c>Scene/Models.slang</c>, written once a frame into the frame's buffer after <see cref="FrameConstants"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuModels
{
    public const int Offset = FrameConstants.Bytes;

    public const int Bytes = 28;

    public uint Instances;

    public uint Count;

    public uint Vertices;

    public uint Indices;

    public uint Materials;

    public uint Meshes;

    public uint Failures;
}

/// <summary>
/// The world as rays see it: the acceleration structure over the instances and the hit materials.
/// </summary>
/// <remarks>
/// The twin of <c>Scene/Models.slang</c> TracedWorld, written once a frame into the frame's buffer after
/// <see cref="GpuModels"/> by what builds the structure, and all 0 where nothing does.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuTracedWorld
{
    public const int Offset = GpuModels.Offset + GpuModels.Bytes;

    public const int Bytes = 8;

    public uint Acceleration;

    public uint HitMaterials;
}

/// <summary>
/// The frame's lights: the sun, the open sky, the sampler of the sky's tables and the light table.
/// </summary>
/// <remarks>
/// The twin of <c>Scene/Lights.slang</c>, written once a frame into the frame's buffer after
/// <see cref="GpuTracedWorld"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuLights
{
    public const int Offset = GpuTracedWorld.Offset + GpuTracedWorld.Bytes;

    public const int Bytes = 68;

    public Vector4 SunDirection;

    public Vector4 SunIlluminance;

    public Vector4 SkyRadiance;

    public float CameraAltitude;

    public uint LutSampler;

    public uint Table;

    public uint Count;

    public uint Tiles;
}

/// <summary>
/// The heap ids of the sky's tables.
/// </summary>
/// <remarks>
/// The twin of <c>Scene/Lights.slang</c> SkyTables, written once a frame into the frame's buffer after
/// <see cref="GpuLights"/> by what makes the tables, and all 0 where nothing does, which the shaders read as no sky.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuSkyTables
{
    public const int Offset = GpuLights.Offset + GpuLights.Bytes;

    public const int Bytes = 12;

    public uint Transmittance;

    public uint MultiScattering;

    public uint View;
}

/// <summary>
/// The frame's shadows: the mask and the sun's tint.
/// </summary>
/// <remarks>
/// The twin of <c>Scene/Shadows.slang</c>, written once a frame into the frame's buffer after
/// <see cref="GpuSkyTables"/> by what traces them, and all 0 where nothing does.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuShadows
{
    public const int Offset = GpuSkyTables.Offset + GpuSkyTables.Bytes;

    public const int Bytes = 8;

    public uint Mask;

    public uint SunTint;
}

/// <summary>
/// The light that reaches the view's surfaces indirectly: the traced diffuse light and reflections and the probe grid.
/// </summary>
/// <remarks>
/// The twin of <c>Light/Indirect.slang</c> IndirectLight, written once a frame into the frame's buffer after
/// <see cref="GpuShadows"/> by what traces them, and all 0 where nothing does.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuIndirectLight
{
    public const int Offset = GpuShadows.Offset + GpuShadows.Bytes;

    public const int Bytes = 56;

    public uint Diffuse;

    public uint Reflections;

    public ProbeGridConstants Grid;
}

/// <summary>
/// The air between the camera and what it sees: the volume of light it scatters and lets through, the haze past it and
/// the clouds.
/// </summary>
/// <remarks>
/// The twin of <c>Light/Air.slang</c> Air, written once a frame into the frame's buffer after
/// <see cref="GpuIndirectLight"/> by what fills the volume, and all 0 where nothing does.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuAir
{
    public const int Offset = GpuIndirectLight.Offset + GpuIndirectLight.Bytes;

    public const int Bytes = 60;

    /// <summary>
    /// The heap ids of the volume and the sampler it is read with.
    /// </summary>
    public uint Volume;

    public uint Sampler;

    /// <summary>
    /// The metres the volume reaches.
    /// </summary>
    public float Distance;

    /// <summary>
    /// The haze: how much of the light a metre of it scatters at its height, the metres above that at which it is half
    /// as thick, how much it scatters onward, and its colour.
    /// </summary>
    public float Density;

    public float Height;

    public float HalfHeight;

    public float Anisotropy;

    public Vector3 Color;

    /// <summary>
    /// The heap id of the sky's clouds as seen from the camera, 0 where there are none.
    /// </summary>
    public uint Clouds;

    /// <summary>
    /// The clouds: how much of the sky they cover, from 0 to 1, the height of their base in metres, and the wind that
    /// carries them, across the ground in metres per second.
    /// </summary>
    public float CloudCover;

    public float CloudHeight;

    public Vector2 Wind;
}

/// <summary>
/// The probe grid: its size, its probes, their depth texture and its sampler, and where it lies in the world, the twin
/// of <c>Light/Probes.slang</c> ProbeGridConstants.
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

    public GpuBuffer Probes;

    public GpuTexture Depth;

    public GpuSampler DepthSampler;

    /// <summary>
    /// Where the first probe rests in the world, and the metres between neighbouring probes.
    /// </summary>
    public Vector3 Origin;

    public Vector3 CellSize;
}
