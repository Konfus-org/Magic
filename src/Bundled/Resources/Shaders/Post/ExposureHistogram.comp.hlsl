// How bright the view is: a histogram of its pixels' luminance in EV100, the exposure taken back off (as it was lit
// with the last frame's), a group per 16 x 16 pixels gathering its own and adding it in. Bin 0 holds what is too dark to
// count. The exposure that adapts to it is Post/ExposureAdapt.comp.hlsl. As it reads every pixel of the lit scene, it
// raises a NaN or an infinity it finds there, which the lighting drew as the failure (Include/Failure.hlsli).

#include "Include/Color.hlsli"
#include "Include/Failure.hlsli"

static const uint Bins = 64u;

struct PassParams
{
    float minEv = -4.0;
    float maxEv = 16.0;
};

groupshared uint Counts[Bins];

[numthreads(16, 16, 1)]
void main(uint3 thread : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    if (index < Bins)
    {
        Counts[index] = 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    [branch] if (all(thread.xy < (uint2)ViewSize))
    {
        float3 color = Hdr.SampleLevel(HdrSampler, (float2(thread.xy) + 0.5) * ViewTexel, 0.0).rgb;
        [branch] if (any(isnan(color)) || any(isinf(color)))
        {
            RaiseFailure(Failures, FailureNaN);
        }

        float nits = Luminance(color) / max(LOAD(Exposure, float4, 0u).x, 1e-12);

        // EV100 = log2(L * S / K) for ISO S = 100 and calibration K = 12.5.
        float ev = log2(max(nits, 1e-12) * 8.0);
        uint bin = nits < 1e-6 ? 0u : 1u + (uint)(saturate((ev - params.minEv) / (params.maxEv - params.minEv)) * (float)(Bins - 2u));
        InterlockedAdd(Counts[bin], 1u);
    }

    GroupMemoryBarrierWithGroupSync();
    if (index < Bins)
    {
        ExposureHistogram.InterlockedAdd(index * 4u, Counts[index]);
    }
}
