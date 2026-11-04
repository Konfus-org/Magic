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
    public const int Bytes = 132;

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

    /// <summary>
    /// What the output shows instead of the picture: a <see cref="DebugView"/>.
    /// </summary>
    public uint DebugView;

    /// <summary>
    /// The global illumination's constants, a <see cref="GiConstants"/>.
    /// </summary>
    public uint Gi;

    /// <summary>
    /// The translucency's constants, a <see cref="TranslucencyConstants"/>.
    /// </summary>
    public uint Translucency;

    /// <summary>
    /// The post passes' constants, a <see cref="PostConstants"/>.
    /// </summary>
    public uint Post;
}

/// <summary>
/// What the output can show instead of the picture, set with <c>set WorldRenderer.debug=&lt;view&gt;</c>.
/// </summary>
internal enum DebugView : uint
{
    None,

    /// <summary>
    /// The global distance field at every surface: green where it is zero there, as it should be, red where it reads
    /// outside and blue inside, the more the farther.
    /// </summary>
    Field,

    /// <summary>
    /// The global distance field two voxels above every surface along its normal: green where it reads that
    /// distance, red where it reads farther out and blue farther in, the more the farther.
    /// </summary>
    FieldAbove,

    /// <summary>
    /// The indirect light alone: what the screen probes give every surface.
    /// </summary>
    Indirect,

    /// <summary>
    /// The world probes' irradiance at every surface, what a card or a ray's end reads.
    /// </summary>
    Probes,

    /// <summary>
    /// The sharp reflections alone, before the surface's Fresnel: what a smooth surface mirrors.
    /// </summary>
    Reflections,

    /// <summary>
    /// The sun's colour through the glass at every surface: white where no pane is in the way.
    /// </summary>
    GlassShadow,
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
