using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Shaders;

namespace WorldRenderer;

/// <summary>
/// What the lighting passes read beyond the frame, the twin of <c>Scene/Lighting.slang</c>'s: the sun above the
/// air, the viewer's height, the air, the light table and its froxel lists, the sky's tables and the fog volumes.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct LightingConstants
{
    public const int Bytes = 128;

    public Vector4 SunIlluminance;

    public Vector4 CameraAltitude;

    public FogConstants Fog;

    public uint Lights;

    public uint LightCount;

    public uint FroxelLists;

    public uint TransmittanceLut;

    public uint MultiScatteringLut;

    public uint SkyViewLut;

    public uint LutSampler;

    public uint FogScattering;

    public uint FogIntegrated;

    public uint FogSampler;

    public uint Hdr;

    public uint HasSky;

    public float Exposure;

    public float Padding0;

    public float Padding1;

    public float Padding2;
}

/// <summary>
/// The frame's air, the twin of <c>Fog/Froxels.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct FogConstants
{
    public Vector3 Albedo;

    public float Density;

    public float Height;

    public float HalfHeight;

    public float Anisotropy;

    public uint IsPresent;
}

/// <summary>
/// A light as the GPU reads it from the light table, 64 bytes: the twin of <c>Scene/Lights.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuLight
{
    public const int Bytes = 64;

    public const uint Point = 0;

    public const uint Spot = 1;

    public const uint Rect = 2;

    public Vector3 Position;

    public float Range;

    public Vector3 Direction;

    public float Radius;

    public Vector3 Radiance;

    public uint Type;

    public float CosOuter;

    public float CosInner;

    public float Width;

    public float Height;
}
