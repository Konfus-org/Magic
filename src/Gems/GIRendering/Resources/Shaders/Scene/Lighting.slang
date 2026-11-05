// What the lighting passes read beyond the frame: the sun above the air, the viewer's height, the sky's tables and the
// HDR picture they light, C#'s LightingConstants, in the buffer whose heap id is the pass header's z.
module "Scene/Lighting";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Sky.ISky;

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
    public uint padding0;
    public uint padding1;
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
