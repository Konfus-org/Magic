// Where each z-bin's lights start in the view's lights, nearest bin first: the counts before it summed (LightStarts),
// and how many the view holds, no more than it has room for. One group, a thread a bin.

#include "Include/LightCulling.hlsli"

groupshared uint Counted[ZBins];

[numthreads(1024, 1, 1)]
void main(uint bin : SV_GroupIndex)
{
    Counted[bin] = LightCounts.Load(bin * 4u);
    GroupMemoryBarrierWithGroupSync();

    // A prefix sum, doubling the stride each step (Hillis & Steele 1986).
    [unroll] for (uint stride = 1u; stride < ZBins; stride <<= 1)
    {
        uint before = bin >= stride ? Counted[bin - stride] : 0u;
        GroupMemoryBarrierWithGroupSync();
        Counted[bin] += before;
        GroupMemoryBarrierWithGroupSync();
    }

    uint count = LightCounts.Load(bin * 4u);
    LightStarts.Store(bin * 4u, Counted[bin] - count);
    if (bin == ZBins - 1u)
    {
        LightCounts.Store(CountHeld * 4u, min(Counted[bin], CountOf(Lights, 80u)));
    }
}
