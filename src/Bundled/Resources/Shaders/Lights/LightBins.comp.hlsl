// The view's z-bins: a thread per light the view holds, nearest first, widening every bin its reach spans to hold it,
// so a bin ends up holding the first and the last light that can reach its depths (Drobot 2017). A bin starts empty,
// its first past its last, from the renderer's ZBinsEmpty.

#include "Include/LightCulling.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= LightCounts.Load(CountHeld * 4u))
    {
        return;
    }

    GpuLight light = LOAD(Lights, GpuLight, thread.x);
    float farthest = light.viewDepth + light.range;
    if (farthest < Near)
    {
        return;
    }

    uint first = ZBinOf(max(light.viewDepth - light.range, Near));
    uint last = ZBinOf(farthest);
    [loop] for (uint bin = first; bin <= last; bin++)
    {
        LightBins.InterlockedMin(bin * 8u, thread.x);
        LightBins.InterlockedMax(bin * 8u + 4u, thread.x);
    }
}
