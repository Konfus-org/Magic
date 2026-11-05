using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.GIRendering;

/// <summary>
/// What the lighting passes read beyond the frame, the twin of <c>Scene/Lighting.slang</c>'s: the sun above the
/// air, the viewer's height, the air, the light table and its froxel lists, the sky's tables and the fog volumes.
/// </summary>
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
    /// What the output shows: a <see cref="DebugView"/>.
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
/// What the output shows, set with <c>set GIRendering.debug=&lt;view&gt;</c>; the twin of <c>Scene/Lighting.slang</c>'s.
/// A view of one part of a surface is black where nothing was drawn.
/// </summary>
internal enum DebugView : uint
{
    /// <summary>
    /// The finished frame.
    /// </summary>
    Final,

    /// <summary>
    /// The surface's base colour.
    /// </summary>
    Albedo,

    /// <summary>
    /// The surface's world normal, each axis from -1 to 1 shown from 0 to 1.
    /// </summary>
    Normal,

    /// <summary>
    /// How far the surface is from the camera, on a log scale: white at the camera.
    /// </summary>
    Depth,

    Roughness,

    Metalness,

    /// <summary>
    /// The material's own occlusion, not what the screen shows of it.
    /// </summary>
    Occlusion,

    /// <summary>
    /// Where every pixel's surface was on last frame's screen: its move since, red across and green down, a tenth of the
    /// screen at full.
    /// </summary>
    Velocity,

    /// <summary>
    /// The indirect light alone: what the probes give every surface.
    /// </summary>
    GI,

    /// <summary>
    /// How lit by the sun every surface is through the sun's pages alone: white in the sun, black in shadow, tinted blue the
    /// coarser the level that answered, magenta where no level had a page.
    /// </summary>
    Shadows,

    /// <summary>
    /// The sharp reflections alone, before the surface's Fresnel: what a smooth surface mirrors. Flat teal on a preset that
    /// traces none.
    /// </summary>
    Reflections,

    /// <summary>
    /// The picture as lit, scaled by the exposure: no resolve over time, bloom, effects or tonemap.
    /// </summary>
    WithoutPost,
}

/// <summary>
/// The frame's air, the twin of <c>Fog/Froxels.slang</c>'s.
/// </summary>
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
