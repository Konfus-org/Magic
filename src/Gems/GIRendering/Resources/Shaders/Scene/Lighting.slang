// What the lighting passes share beyond the frame: the sun above the air, the viewer's height, the air, the light
// table and its froxel lists, the sky's tables, the fog volumes and the global illumination's constants, C#'s
// LightingConstants, in the buffer whose heap id is the pass header's z.
module "Scene/Lighting";

import Core.Heap;
import Core.Pass;
import Fog.Froxels;

public struct LightingConstants
{
    // Colour times intensity, in lux, before the air.
    public float4 sunIlluminance;
    // Metres above the ground in x.
    public float4 cameraAltitude;
    public FogConstants fog;
    public uint lights;
    public uint lightCount;
    public uint froxelLists;
    public uint transmittanceLut;
    public uint multiScatteringLut;
    public uint skyViewLut;
    public uint lutSampler;
    public uint fogScattering;
    public uint fogIntegrated;
    public uint fogSampler;
    public uint hdr;
    public uint hasSky;
    // What the tonemap scales the HDR picture by.
    public float exposure;
    // What the output shows, a DebugView.
    public uint debugView;
    // The global illumination's constants, C#'s GiConstants.
    public uint gi;
    // The translucency's constants, C#'s TranslucencyConstants.
    public uint translucency;
    // The post passes' constants, C#'s PostConstants.
    public uint post;
}

public LightingConstants GetLighting() { return LoadConstants<LightingConstants>(GetPassHeader().z); }

// What the output can show, C#'s DebugView.
public enum DebugView : uint
{
    Final,
    Albedo,
    Normal,
    Depth,
    Roughness,
    Metalness,
    Occlusion,
    Velocity,
    GI,
    Shadows,
    Reflections,
    WithoutPost,
}

// What a debug view shows where the world has no such effect.
public static const float3 NothingColor = float3(0.0, 0.5, 0.5);

public DebugView GetDebugView(LightingConstants lighting) { return (DebugView)lighting.debugView; }

// Whether a view lights the picture with one part of the light alone: it has no sky or air, and says nothing of how
// bright the picture is.
public bool IsLightPartView(DebugView view)
{
    return view == DebugView.GI || view == DebugView.Shadows || view == DebugView.Reflections;
}
