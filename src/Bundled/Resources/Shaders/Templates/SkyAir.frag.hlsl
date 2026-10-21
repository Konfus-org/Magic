// The sky's air template: one triangle over a layer of the view's SkyAir, a slice of distance an iteration
// (Include/SkyAir.hlsli), each texel the air between the camera and that far along its way through the view, the
// world's sky's EvaluateAir (Include/Sky.hlsli), composed before this from the frame's sun and weather as the sky seen
// is. Without a sky (SKY_FALLBACK) the renderer's own has no air, so nothing is scattered and all of it gets through.

#include "Include/Fullscreen.hlsli"
#include "Include/SkyAir.hlsli"
#include "Include/SkyFrame.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
#if SKY_FALLBACK
    return float4(0.0, 0.0, 0.0, 1.0);
#else
    float3 direction = normalize(ViewToWorld(ViewPosition(input.uv * ViewSize, 1.0)));
    float reach = SkyAirReach(PassIteration());
    AirLight air = EvaluateAir(SkyInputsOf(direction, false, 0.5), reach, LoadMaterialParams(SkyMaterial));
    return float4(air.nits, air.through);
#endif
}
