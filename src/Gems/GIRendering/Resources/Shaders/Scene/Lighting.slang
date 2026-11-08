// What the lighting passes read beyond the frame: the sun above the air, the viewer's height, the sky's tables, the HDR
// picture they light, the light table and its tiles, and the shadow masks, C#'s LightingConstants, in the buffer whose heap id is the
// pass header's z.
module "Scene/Lighting";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Sky.ISky;

// The sun's angular radius as it looks from the Earth: how wide a disc its shadow rays aim through.
public static const float SunAngularRadius = 0.00465;

public struct LightingConstants
{
    // Colour times intensity, in lux, before the air.
    public float4 sunIlluminance;
    // Metres above the ground in x.
    public float4 cameraAltitude;
    public uint transmittanceLut;
    public uint multiScatteringLut;
    public uint skyViewLut;
    public uint lutSampler;
    // Whether a sky entity draws the sky; the flat fallback where none does.
    public uint hasSky;
    public uint hdr;
    public uint lights;
    public uint lightCount;
    public uint lightTiles;
    // The denoised mask shading reads; 0 on a GPU that traces no rays: nothing is shadowed.
    public uint shadowMask;
    // The mask and the sun's tiles as the rays left them, for the denoiser.
    public uint tracedShadows;
    public uint sunTiles;
    // The traced reflections' light at half the picture's size, blended over frames; 0 where none are traced. Read
    // through Scene/TracedReflections.
    public uint reflections;
    // The colour the sun's light takes on through glass, read through Scene/Shadows; 0 on a GPU that traces no rays.
    public uint sunTint;
    public uint padding1;
    public uint padding2;
}

public LightingConstants GetLighting() { return LoadConstants<LightingConstants>(GetPassHeader().z); }

// What a sky is handed to answer for a direction from the camera.
public SkyInputs CreateSkyInputs(FrameConstants frame, LightingConstants lighting, float3 direction, bool hasSunDisc)
{
    SkyInputs sky;
    sky.viewDirection = direction;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.hasSunDisc = hasSunDisc;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;
    return sky;
}
