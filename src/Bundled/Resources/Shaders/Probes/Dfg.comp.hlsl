// The DFG table every shading reads at (n.v, roughness): Core's IntegrateDfg (Include/Brdf.hlsli), a texel a thread. It
// is small enough to make every frame rather than keep.

#include "Include/Brdf.hlsli"

static const uint SampleCount = 256u;

[numthreads(8, 8, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    uint width;
    uint height;
    Dfg.GetDimensions(width, height);
    if (thread.x >= width || thread.y >= height)
    {
        return;
    }

    float2 at = (float2(thread.xy) + 0.5) / float2(width, height);
    Dfg[thread.xy] = float4(IntegrateDfg(at.x, at.y, SampleCount), 1.0);
}
