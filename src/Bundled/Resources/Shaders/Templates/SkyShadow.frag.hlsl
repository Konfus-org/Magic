// The sky's shadow template: one triangle over the frame's SkyShadow, each texel the share of the key light's light, by
// colour, that reaches its place of the ground around the camera (Include/SkyShadow.hlsli) through the world's sky, its
// material's sky shader's LightThrough (Include/Sky.hlsli), composed before this from the frame's sun, moon and weather
// as the sky seen is. Without a sky (SKY_FALLBACK) the renderer's own has no air and nothing in front of the light, so
// all of it gets through while it is up, fading out as it nears the horizon, which hides it.

#include "Include/Fullscreen.hlsli"
#include "Include/SkyFrame.hlsli"
#include "Include/SkyShadow.hlsli"

float3 main(PassVaryings input) : SV_Target0
{
#if SKY_FALLBACK
    return saturate((0.0087 - KeyLightDirection.y) / 0.035).xxx;
#else
    float2 place = SkyShadowPlace(input.uv);
    float3 position = float3(place.x, CameraPosition.y, place.y);
    return LightThrough(SkyInputsOf(-KeyLightDirection, false, 0.5), position, -KeyLightDirection, LoadMaterialParams(SkyMaterial));
#endif
}
