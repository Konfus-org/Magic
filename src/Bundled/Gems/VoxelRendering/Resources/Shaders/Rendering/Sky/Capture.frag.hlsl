// Captures the world's sky into one face of the first level of a cube, which the light from everywhere and the
// reflections of the sky are made from (Skies.hlsli, composed before this). Its sun's and moon's discs are left out,
// which their own light stands for, and so is what it gives off pre-exposed, which is never captured.

#include "Include/Cube.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Rendering/Include/SkyAlong.hlsli"

struct CaptureConstants
{
    uint view;
    uint face;               // the face drawn, as Include/Cube.hlsli numbers them
};

CONSTANTS(CaptureConstants, c);

float4 main(PassVaryings input) : SV_Target0
{
    LoadView(c.view);
    SkyLight light = SkyAlong(CubeDirection(c.face, input.uv), true, 0.5);
    return float4(light.nits, 1.0);
}
