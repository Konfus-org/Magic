// The sun's pages from frame to frame: receivers mark the pages they need from the depth buffer, instances that
// moved invalidate the pages under them, pages no one marked go back to the pool, pages marked and missing come out
// of it and wait to be drawn, those are cleared before the draw and counted valid after it. The pass header's x is
// the frame's constants and y the shadow constants.
module "ShadowPages";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Shadow.Virtual;

// Each pixel of the depth buffer asks for the page its receiver is in, at the level its footprint picks.
[shader("compute")]
[numthreads(8, 8, 1)]
void ShadowMarkPages(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    if (depth <= 0.0)
        return;

    float3 world = ReconstructWorld(frame, GetTexelCenterUv(thread.xy, size), depth);
    uint level = GetLevel(shadow, GetFootprint(shadow, ToViewDepth(depth, frame.near)));
    int2 page = GetPage(shadow, level, ToLight(shadow, world).xy);
    if (!IsInWindow(shadow, level, page))
        return;

    ArrayRef<uint> requests = { shadow.pageRequests };
    requests[GetCell(shadow, level, page)] = PackPage(page);
}

// Each instance that changed marks the pages under where it was and where it is to be drawn again.
[shader("compute")]
[numthreads(64, 1, 1)]
void ShadowInvalidatePages(uint3 thread : SV_DispatchThreadID)
{
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    if (thread.x >= shadow.dirtyCount)
        return;

    ArrayRef<DirtyInstance> dirty = { shadow.dirtyInstances };
    DirtyInstance instance = dirty[thread.x];
    InvalidateSphere(shadow, instance.oldCenter, instance.oldRadius);
    InvalidateSphere(shadow, instance.newCenter, instance.newRadius);
}

void InvalidateSphere(ShadowConstants shadow, float3 center, float radius)
{
    if (radius <= 0.0)
        return;

    BufferRef table = { shadow.pageTable };
    float2 light = ToLight(shadow, center).xy;
    for (uint level = 0u; level < shadow.levels; level++)
    {
        int2 first;
        int2 last;
        if (!GetPageRange(shadow, level, light, radius, first, last))
            continue;

        for (int y = first.y; y <= last.y; y++)
        {
            for (int x = first.x; x <= last.x; x++)
            {
                int2 page = int2(x, y);
                uint cell = GetCell(shadow, level, page);
                uint2 entry = table.Load<uint2>(cell * 8u);
                if (IsAllocated(entry) && entry.y == PackPage(page))
                    table.InterlockedOr(cell * 8u, ShadowEntryInvalid);
            }
        }
    }
}

// The pages of the window a sphere on the light's plane touches at a level; false when none.
public bool GetPageRange(ShadowConstants shadow, uint level, float2 light, float radius, out int2 first, out int2 last)
{
    float pageWorld = GetPageWorld(shadow, level);
    int2 origin = shadow.windows[level].xy;
    int2 end = origin + int(shadow.tableWidth) - 1;
    first = max(int2(floor((light - radius) / pageWorld)), origin);
    last = min(int2(floor((light + radius) / pageWorld)), end);
    return all(first <= last);
}

// Each cell whose page no receiver marked this frame, or marked another page for, gives its page back.
[shader("compute")]
[numthreads(64, 1, 1)]
void ShadowFreePages(uint3 thread : SV_DispatchThreadID)
{
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    uint cell = thread.x;
    if (cell >= shadow.levels * shadow.tableWidth * shadow.tableWidth)
        return;

    ArrayRef<uint2> table = { shadow.pageTable };
    uint2 entry = table[cell];
    if (!IsAllocated(entry))
        return;

    ArrayRef<uint> requests = { shadow.pageRequests };
    uint requested = requests[cell];
    if (requested != ShadowNoRequest && requested == entry.y)
        return;

    BufferRef freeList = { shadow.freeList };
    uint slot = freeList.InterlockedAdd(0u, 1u);
    freeList.Store<uint>((slot + 1u) * 4u, GetPhysical(entry));
    table[cell] = uint2(0u, 0u);
}

// Each cell marked this frame and without a page takes one from the pool, if any is left, and every marked page
// waiting to be drawn again joins the list of invalid pages.
[shader("compute")]
[numthreads(64, 1, 1)]
void ShadowAllocatePages(uint3 thread : SV_DispatchThreadID)
{
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    uint cell = thread.x;
    uint cellsPerLevel = shadow.tableWidth * shadow.tableWidth;
    if (cell >= shadow.levels * cellsPerLevel)
        return;

    ArrayRef<uint> requests = { shadow.pageRequests };
    uint requested = requests[cell];
    if (requested == ShadowNoRequest)
        return;

    ArrayRef<uint2> table = { shadow.pageTable };
    uint2 entry = table[cell];
    if (!IsAllocated(entry))
    {
        BufferRef freeList = { shadow.freeList };
        uint count = freeList.InterlockedAdd(0u, 0xFFFFFFFFu);
        if (count == 0u || count > 0x7FFFFFFFu)
        {
            freeList.InterlockedAdd(0u, 1u);
            return;
        }

        uint physical = freeList.Load<uint>(count * 4u);
        entry = uint2((physical + 1u) | ShadowEntryInvalid, requested);
        table[cell] = entry;
    }

    if (!IsInvalid(entry))
        return;

    BufferRef counts = { shadow.counts };
    uint slot = counts.InterlockedAdd(ShadowCountInvalidPages, 1u);
    if (slot >= shadow.maxInvalidPages)
        return;

    ArrayRef<uint2> invalid = { shadow.invalidPages };
    invalid[slot] = uint2(GetPhysical(entry), ((cell / cellsPerLevel) << 16u) | (cell % cellsPerLevel));
    BufferRef clearArguments = { shadow.clearArguments };
    clearArguments.InterlockedMax(ShadowClearGroupsZ, slot + 1u);
}

// Every texel of each invalid page set to the farthest depth, before the page is drawn.
[shader("compute")]
[numthreads(8, 8, 1)]
void ShadowClearPages(uint3 thread : SV_DispatchThreadID)
{
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    BufferRef counts = { shadow.counts };
    if (thread.z >= min(counts.Load<uint>(ShadowCountInvalidPages), shadow.maxInvalidPages))
        return;

    ArrayRef<uint2> invalid = { shadow.invalidPages };
    uint2 page = invalid[thread.z];
    UintStorageRef pool = { shadow.pool, 0u };
    pool.Store(GetPoolOrigin(shadow, page.x) + thread.xy, ShadowClearKey);
}

// Each invalid page counts as drawn once the draw has run.
[shader("compute")]
[numthreads(1, 1, 1)]
void ShadowFinishPages(uint3 thread : SV_DispatchThreadID)
{
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    BufferRef counts = { shadow.counts };
    if (thread.z >= min(counts.Load<uint>(ShadowCountInvalidPages), shadow.maxInvalidPages))
        return;

    ArrayRef<uint2> invalid = { shadow.invalidPages };
    uint2 page = invalid[thread.z];
    uint cell = ((page.y >> 16u) * shadow.tableWidth * shadow.tableWidth) + (page.y & 0xFFFFu);
    ArrayRef<uint2> table = { shadow.pageTable };
    uint2 entry = table[cell];
    entry.x &= ~ShadowEntryInvalid;
    table[cell] = entry;
}
