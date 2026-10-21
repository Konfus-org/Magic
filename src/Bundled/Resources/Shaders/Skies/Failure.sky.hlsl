// The sky a broken one is drawn as, which its failure material, Materials/Failures/Sky.mat, names and a renderer draws
// in a broken sky's place (Assets.Failure<Material>): magenta breathing in the view, given off as it is so it shows
// whatever the exposure, as the failure surface does, and never taken for a real sky. Into the light from everywhere
// it is a plain grey sky as bright as the sun makes it, so the world it lights stays lit as if the sky were there,
// rather than taking on the failure's colour.

#include "Include/Color.hlsli"
#include "Include/Failure.hlsli"
#include "Include/Sky.hlsli"

struct MaterialParams
{
    float captured = 0.03;  // the grey sky's nits per lux of the sun, as it is captured
};

SkyLight EvaluateSky(SkyInputs input, MaterialParams material)
{
    SkyLight light;
    light.nits = float3(1.0, 1.0, 1.0) * (Luminance(input.sunIlluminance) * material.captured);
    light.discs = float3(0.0, 0.0, 0.0);
    light.emissive = float3(0.0, 0.0, 0.0);
    if (!input.isCapture)
    {
        light.nits = float3(0.0, 0.0, 0.0);
        light.emissive = FailurePixel(input.time);
    }

    return light;
}

// No air and nothing in front of the light, so the world stays lit.
float3 LightThrough(SkyInputs input, float3 position, float3 towardLight, MaterialParams material)
{
    return float3(1.0, 1.0, 1.0);
}

// No air, so the world is seen as it is.
AirLight EvaluateAir(SkyInputs input, float reach, MaterialParams material)
{
    AirLight air;
    air.nits = float3(0.0, 0.0, 0.0);
    air.through = 1.0;
    return air;
}
