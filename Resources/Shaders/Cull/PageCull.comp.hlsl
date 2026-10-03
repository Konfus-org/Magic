// One thread per page: a page with instances whose cell's box touches the frustum is appended to the list
// the instance cull dispatches one group per entry over. Cell 0 is the dynamic cell and always visible.
// DispatchArgs is that dispatch's indirect arguments: the CPU resets it to (0, 1, 1) every frame and x
// counts the pages appended here. Its fourth word is how many pages there are: the page buffer is larger
// than that, and what lies past them was never written.

#include "Cull/Common.hlsli"

StructuredBuffer<GpuPage> Pages : READ(0);
StructuredBuffer<GpuCell> Cells : READ(1);
RWStructuredBuffer<uint> VisiblePages : WRITE(0);
RWStructuredBuffer<uint> DispatchArgs : WRITE(1);

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= DispatchArgs[3])
        return;

    GpuPage page = Pages[threadId.x];
    if (page.count == 0u)
        return;

    GpuCell cell = Cells[page.cell];
    if (page.cell != 0u && !BoxInFrustum(cell.aabbMin.xyz, cell.aabbMax.xyz))
        return;

    uint index;
    InterlockedAdd(DispatchArgs[0], 1u, index);
    VisiblePages[index] = threadId.x;
}
