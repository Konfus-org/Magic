// How bright the view is: a histogram of its pixels' luminance in EV100, the exposure taken back off (as it was lit
// with the last frame's), a group per 16 x 16 pixels gathering its own and adding it in. Bin 0 holds what is too dark to
// count. The exposure that adapts to it is Post/ExposureAdapt.comp.hlsl.

#include "Include/Color.hlsli"

#define BINS 64u

struct PassParams
{
    float minEv = -4.0;
    float maxEv = 16.0;
};

groupshared uint Counts[BINS];

[numthreads(16, 16, 1)]
void main(uint3 thread : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    if (index < BINS)
    {
        Counts[index] = 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    [branch] if (all(thread.xy < (uint2)ViewSize))
    {
        float3 color = Hdr.SampleLevel(HdrSampler, (float2(thread.xy) + 0.5) * ViewTexel, 0.0).rgb;
        float nits = Luminance(color) / max(LOAD(Exposure, float4, 0u).x, 1e-12);

        // EV100 = log2(L * S / K) for ISO S = 100 and calibration K = 12.5.
        float ev = log2(max(nits, 1e-12) * 8.0);
        uint bin = nits < 1e-6 ? 0u : 1u + (uint)(saturate((ev - params.minEv) / (params.maxEv - params.minEv)) * (float)(BINS - 2u));
        InterlockedAdd(Counts[bin], 1u);
    }

    GroupMemoryBarrierWithGroupSync();
    if (index < BINS)
    {
        ExposureHistogram.InterlockedAdd(index * 4u, Counts[index]);
    }
}
