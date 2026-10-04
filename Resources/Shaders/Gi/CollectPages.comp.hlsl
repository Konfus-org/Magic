// One thread per page: a page whose cell's box touches the level being rebuilt is listed for CollectInstances,
// which dispatches one group per listed page (PageArgs, reset by the CPU each frame to (0, 1, 1, pageCount)).
// Cell 0 is the dynamic cell and always listed.

#include "Include/Shade.hlsli"
#include "Gi/Common.hlsli"

StructuredBuffer<GpuPage> Pages : READ(0);
StructuredBuffer<GpuCell> Cells : READ(1);
RWStructuredBuffer<uint> PageList : WRITE(0);
RWStructuredBuffer<uint> PageArgs : WRITE(1);

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= PageArgs[3])
        return;

    GpuPage page = Pages[threadId.x];
    if (page.count == 0u)
        return;

    uint level = GiUpdateLevel;
    float3 levelMin = GiOrigin(level);
    float3 levelMax = levelMin + GiExtent(level);
    GpuCell cell = Cells[page.cell];
    if (page.cell != 0u && (any(cell.aabbMax.xyz < levelMin) || any(cell.aabbMin.xyz > levelMax)))
        return;

    uint index;
    InterlockedAdd(PageArgs[0], 1u, index);
    PageList[index] = threadId.x;
}
