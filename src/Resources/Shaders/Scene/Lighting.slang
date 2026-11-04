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
    // What the output shows instead of the picture, C#'s DebugView: 0 the picture, 1 the field at every surface, 2
    // the field two voxels above every surface, 3 the indirect light, 4 the world probes, 5 the reflections, 6 the
    // glass shadow, 7 the sun, 8 the cards, 9 the occlusion, 10 the velocity.
    public uint debugView;
    // The global illumination's constants, C#'s GiConstants.
    public uint gi;
    // The translucency's constants, C#'s TranslucencyConstants.
    public uint translucency;
    // The post passes' constants, C#'s PostConstants.
    public uint post;
}

public LightingConstants GetLighting() { return LoadConstants<LightingConstants>(GetPassHeader().z); }

// Whether a debug view shows a factor rather than a light: the glass shadow, the sun, the occlusion and the
// velocity are shown as they are, with no air, exposure, bloom or tonemap over them.
public bool IsFactorView(uint debugView) { return debugView == 6u || debugView == 7u || debugView >= 9u; }
