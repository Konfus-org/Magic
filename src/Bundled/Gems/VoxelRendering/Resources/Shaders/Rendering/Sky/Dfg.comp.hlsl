// The DFG table every shading reads at (n.v, roughness): Core's IntegrateDfg (Include/Brdf.hlsli), a texel a thread.

#include "Include/Brdf.hlsli"
#include "Include/Heap.hlsli"

struct DfgConstants
{
    uint dfg;                // written
};

CONSTANTS(DfgConstants, c);

static const uint SampleCount = 256u;

[numthreads(8, 8, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    uint2 size = SizeOf(LEVEL(c.dfg, 0u));
    if (any(thread.xy >= size))
    {
        return;
    }

    float2 at = (float2(thread.xy) + 0.5) / float2(size);
    LEVEL(c.dfg, 0u)[thread.xy] = float4(IntegrateDfg(at.x, at.y, SampleCount), 1.0);
}
