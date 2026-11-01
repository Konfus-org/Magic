// What a sky is written against, whatever renderer draws it: the sky as seen along a direction, from the world's sun,
// its moon and its weather, which the renderer hands it. A sky is a module whose struct, named like its file,
// implements ISky; its fields are its parameters, as a surface's are (Core/Surface), and a material gives their values.
//
// It is drawn twice a frame: over what nothing was drawn on in each view, and into the cube the world's light from
// everywhere and the reflections of the sky are made from (isCapture). So it lights the world as it looks. The sun's
// and the moon's discs are handed back apart from the rest: the renderer shows them in the view and leaves them out of
// the capture, where their own light already stands for them, so no sky counts either twice.
//
// A sky never deals with failure: a material whose sky does not compile, or whose shader is a surface, is drawn with
// Skies/Failure instead, chosen by the renderer, as a broken surface is drawn with the failure surface.

module "Core/Sky";

__exported import Core.Color;
__exported import Core.Heap;
__exported import Core.Math;
__exported import Core.Pass;
__exported import Core.Surface;
__exported import Core.Weather;

// The sky's sun and moon, which a renderer hands a sky whether or not either lights the world now. One the world has
// none of has no light and points straight down.
public struct CelestialConstants
{
    float3 sunDirection;     // unit length, the way the sun's light travels: the sun is at -sunDirection
    float sunAngularRadius;  // radians, the sun's disc
    float3 sunIlluminance;   // lux, times the sun's colour, above the air; zero with no sun
    float moonAngularRadius; // radians, the moon's disc
    float3 moonDirection;    // unit length, the way the moon's light travels: the moon is at -moonDirection
    float3 moonIlluminance;  // lux, times the moon's colour, of a full moon above the air; zero with no moon
}

// What the renderer hands a sky, in world space, +Y up.
public struct SkyInputs
{
    float3 direction;        // unit length, the way this point of the sky is seen
    float3 directionDdx;     // how direction changes a pixel across and a pixel down: a sky has no implicit
    float3 directionDdy;     // derivatives, as a renderer may draw it in compute
    float3 cameraPosition;   // metres, for a sky that changes as the camera climbs
    FrameConstants frame;    // the clocks: what drifts moves by frame.worldSec, which stands still while paused
    CelestialConstants celestial;
    WeatherConstants weather;
    bool isCapture;          // drawn into the light from everywhere, coarse and blurred, so detail can be spared
    float dither;            // 0 to 1, another for each pixel and frame in the view, which the temporal upscale blends,
                             // so a sky that integrates by steps offsets them by it; the same everywhere otherwise
    SamplerRef sampler;      // what the renderer reads materials' textures through

    // A texture at uv with uv's own derivatives across the pixel, which a sky works out from directionDdx and
    // directionDdy; white while it is none.
    public float4 SampleGrad(TextureRef texture, float2 uv, float2 uvDdx, float2 uvDdy)
    {
        return SampleMaterialTexture(texture, sampler, uv, uvDdx, uvDdy);
    }
}

// What a sky hands back along a direction, in nits, not pre-exposed: it lights the world as the sky would.
public struct SkyRadiance
{
    float3 nits;             // the sky's luminance along the direction, without the sun's and the moon's discs
    float3 discs;            // the sun's and the moon's discs as seen along it, through whatever is in front of them;
                             // zero off them
    float3 emissive;         // shown as it is in the view and never captured, pre-exposed: 1 reads as white whatever
                             // the exposure, as a surface's emissive does
}

public interface ISky
{
    SkyRadiance Evaluate(SkyInputs input);
}
