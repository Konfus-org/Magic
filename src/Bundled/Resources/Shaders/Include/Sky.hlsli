// What a sky shader (a .sky.hlsl) is written against, whatever renderer draws it: the sky as seen along a direction,
// from the world's sun and its weather, which the renderer hands it. A renderer composes the sky into its own shaders
// as it does a surface: it binds the textures and the material's values, generates the function that loads them, and
// defines SampleTextureGrad (Include/Surface.hlsli), so a sky can sample a texture, an image of a sky, at the
// derivatives it works out itself.
//
// A sky shader declares exactly three things:
//
//   struct MaterialParams { ... };                     // its parameters, as a surface's are; a .mat file gives values
//   SkyLight EvaluateSky(SkyInputs input, MaterialParams material);
//   float SunThrough(SkyInputs input, float3 position, MaterialParams material);
//
// It is drawn twice a frame: over what nothing was drawn on in each view, and into the cube the world's light from
// everywhere and the reflections of the sky are made from (isCapture). So it lights the world as it looks. The sun's
// disc is handed back apart from the rest: the renderer shows it in the view and leaves it out of the capture, where
// the sun's own light already stands for it, so no sky counts the sun twice.
//
// SunThrough is the sky's shadow on the world: the share of the sun's light that reaches a place (metres, world space)
// through what the sky has in front of the sun, 1 for all of it; input.direction means nothing to it. The renderer
// lays it over the ground around the camera and lights the world by it, so it must agree with what EvaluateSky shows
// in front of the sun's disc. A sky with nothing in front of the sun returns 1.
//
// A sky never deals with failure: a material whose sky does not compile, or whose shader is a surface, is drawn with
// Skies/Failure.sky.hlsl instead, chosen by the renderer, as a broken surface is drawn with the failure surface.

#ifndef MAGIC_SKY_HLSLI
#define MAGIC_SKY_HLSLI

#include "Include/Math.hlsli"
#include "Include/Surface.hlsli"

// What the renderer hands a sky, in world space, +Y up.
struct SkyInputs
{
    float3 direction;        // unit length, the way this point of the sky is seen
    float3 cameraPosition;   // metres, for a sky that changes as the camera climbs
    float time;              // seconds, for what drifts
    float3 sunDirection;     // unit length, the way the sun's light travels: the sun is at -sunDirection
    float3 sunIlluminance;   // lux, times the sun's colour; zero with no sun
    float sunAngularRadius;  // radians, the sun's disc as the world's light has it
    float3 wind;             // metres per second, the world's weather
    float coverage;          // how much of the sky clouds cover, 0 clear to 1 overcast
    float density;           // how thick they are, 0 wisps to 1 thick
    float3 cloudColor;       // linear, the clouds where the sun lights them, times the sun's light
    float3 cloudShadow;      // linear, the clouds in their own shade and underneath, times the sky's light
    bool isCapture;          // drawn into the light from everywhere, coarse and blurred, so detail can be spared
    float dither;            // 0 to 1, another for each pixel and frame in the view, which the temporal upscale blends,
                             // so a sky that integrates by steps offsets them by it; the same everywhere otherwise
};

// What a sky hands back, in nits, not pre-exposed: it lights the world as the sky would.
struct SkyLight
{
    float3 nits;             // the sky's luminance along the direction, without the sun's disc
    float3 sun;              // the sun's disc as seen along it, through whatever is in front of it; zero off it
    float3 emissive;         // shown as it is in the view and never captured, pre-exposed: 1 reads as white whatever
                             // the exposure, as a surface's emissive does
};

#endif
