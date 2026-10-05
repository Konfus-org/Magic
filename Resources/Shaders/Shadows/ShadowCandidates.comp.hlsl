// Lists the lights that could hold shadow pages this frame, one thread per light in parallel: every spot or
// point light that may cast, with its score (how much of the main view it could reach: its range on screen, 1
// for a light the camera is inside; twin: LocalShadows.Score), appended to the candidates buffer the selection
// pass ranks. Thousands of lights become a short list, so the selection's one group has little to scan.

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"

static const uint MaxCandidates = 1023u;

StructuredBuffer<GpuLight> Lights : READ(0);
StructuredBuffer<GpuCounts> Counts : READ(1);
RWStructuredBuffer<uint> ShadowCandidates : WRITE(0); // [0] the count, then pairs: the light's row, its score's bits

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= Counts[0].lightCount)
        return;

    GpuLight light = Lights[index];
    if ((light.shadow.z & LightCastsShadows) == 0u || light.positionRange.w <= 0.0)
        return;

    float reach = length(light.positionRange.xyz - CameraPos);
    float score = reach <= light.positionRange.w ? 1.0 : light.positionRange.w * ProjScale.y / reach;
    if (score <= 0.0)
        return;

    uint place;
    InterlockedAdd(ShadowCandidates[0], 1u, place);
    if (place >= MaxCandidates)
        return;

    ShadowCandidates[1u + place * 2u] = index;
    ShadowCandidates[2u + place * 2u] = asuint(score);
}
