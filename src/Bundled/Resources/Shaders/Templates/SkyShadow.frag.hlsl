// The sky's shadow template: one triangle over the frame's SkyShadow, each texel the share of the sun's light that
// reaches its place of the ground around the camera (Include/SkyShadow.hlsli) through the world's sky, its material's
// sky shader's SunThrough (Include/Sky.hlsli), composed before this from the frame's sun and weather as the sky seen
// is. Without a sky (SKY_FALLBACK) the renderer's own has nothing in front of the sun, and all of it gets through.

#include "Include/Fullscreen.hlsli"
#include "Include/SkyFrame.hlsli"
#include "Include/SkyShadow.hlsli"

float main(PassVaryings input) : SV_Target0
{
#if SKY_FALLBACK
    return 1.0;
#else
    float2 place = SkyShadowPlace(input.uv);
    return SunThrough(SkyInputsOf(-SunDirection, false, 0.5), float3(place.x, CameraPosition.y, place.y), LoadMaterialParams(SkyMaterial));
#endif
}
