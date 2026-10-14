// Empties this frame's table of point and spot lights' pages, a thread an entry, for the views to put in the pages they
// want (ShadowMark): no page, no key, never wanted, no view.

#include "Include/VirtualShadows.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    GpuSunClipmap sun = SunClipmapOf();
    if (id.x < sun.lightEntries)
    {
        ShadowTables.Store4((sun.lightTable + id.x) * 16u, uint4(0u, EmptyKey, 0u, NoView));
    }
}
