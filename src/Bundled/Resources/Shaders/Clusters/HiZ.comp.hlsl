// Builds a level of the view's depth pyramid (HiZ), a thread a texel, a run of the pass each, with as many groups as
// the level has texels (HiZArgs): level 0 from the view's depth, each level after from the one before, every texel the
// farthest of the four under it, which, reverse-Z, is the least. A level whose size is odd covers its last row or
// column with the texel before it, so nothing is left out.

#include "Include/Culling.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint level = PassIteration();
    uint2 size = HiZSize(level);
    if (any(id.xy >= size))
    {
        return;
    }

    uint2 under = id.xy * 2u;
    float farthest;
    if (level == 0u)
    {
        uint2 last = (uint2)ViewSize - 1u;
        farthest = min(min(Depth.Load(int3(min(under, last), 0)), Depth.Load(int3(min(under + uint2(1u, 0u), last), 0))),
            min(Depth.Load(int3(min(under + uint2(0u, 1u), last), 0)), Depth.Load(int3(min(under + 1u, last), 0))));
    }
    else
    {
        uint2 below = HiZSize(level - 1u);
        uint offset = HiZOffset(level - 1u);
        uint2 last = below - 1u;
        uint2 a = min(under, last);
        uint2 b = min(under + uint2(1u, 0u), last);
        uint2 c = min(under + uint2(0u, 1u), last);
        uint2 d = min(under + 1u, last);
        farthest = min(min(asfloat(HiZ.Load((offset + a.y * below.x + a.x) * 4u)), asfloat(HiZ.Load((offset + b.y * below.x + b.x) * 4u))),
            min(asfloat(HiZ.Load((offset + c.y * below.x + c.x) * 4u)), asfloat(HiZ.Load((offset + d.y * below.x + d.x) * 4u))));
    }

    // Never, as a level is never that deep, but it keeps the depth bound with its sampler, though read texel by texel.
    farthest += level > 64u ? KeepReadsAlive(float2(0.0, 0.0)) : 0.0;
    HiZ.Store((HiZOffset(level) + id.y * size.x + id.x) * 4u, asuint(farthest));
}
