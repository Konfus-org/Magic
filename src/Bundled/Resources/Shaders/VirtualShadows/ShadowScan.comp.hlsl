// Keeps the sun's entries in step with its clipmap, a thread an entry: an entry whose level's window moved off the page
// it held lets that page go; a page is drawn again, all of it when the sun turned or what depth is measured from moved,
// else where a sphere that moved reaches its column (the sun is far, so a caster's shadow falls straight along it),
// border and all; a page wanted this frame is noted as read; and no page has a view until the allocator gives it one.

#include "Include/VirtualShadows.hlsli"

// The rectangle of a page the spheres that moved cover, each a square about where it is across the sun, from the page's
// place in its level's window; none when they all miss it.
uint TouchedRect(GpuSunClipmap sun, GpuSunLevel level, int2 local, uint count)
{
    uint rect = 0u;
    float2 corner = (float2)local * level.pageSize;
    float perPage = (float)PageContent / ((float)PageTexels * level.pageSize);
    float border = (float)PageBorder / (float)PageTexels;
    [loop] for (uint i = 0u; i < count; i++)
    {
        float4 moved = LOAD(Moved, float4, i);
        float2 across = float2(dot(moved.xyz, sun.right), dot(moved.xyz, sun.up)) + level.camera - corner;
        float2 low = (across - moved.w) * perPage + border;
        float2 high = (across + moved.w) * perPage + border;
        rect = UnionRect(rect, RectOf(low, high, PageTexels));
    }

    return rect;
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    GpuSunClipmap sun = SunClipmapOf();
    uint level = id.x / SunEntriesPerLevel;
    if (level >= sun.levels)
    {
        return;
    }

    GpuSunLevel row = SunLevelOf(level);
    ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.sunTable + id.x);
    int2 page = SunPageAt(row, id.x);
    if (HasPage(entry.page))
    {
        uint record = RecordOf(sun, entry.page);
        bool isOwned = ShadowTables.Load(record * 16u) == id.x + 1u;
        if (entry.tag != SunTagOf(page) || !isOwned)
        {
            if (isOwned)
            {
                ShadowTables.Store2(record * 16u, uint2(0u, 0u));
            }

            entry.page = 0u;
        }
        else
        {
            uint rect = sun.allDirty != 0u ? WholeRect : TouchedRect(sun, row, page - row.origin, sun.moved);
            if (rect != 0u)
            {
                entry.page |= PageDirty;
                ShadowTables.Store(record * 16u + 8u, UnionRect(ShadowTables.Load(record * 16u + 8u), rect));
            }

            if (entry.wanted == sun.frame)
            {
                ShadowTables.Store(record * 16u + 4u, sun.frame);
            }
        }
    }

    entry.view = NoView;
    ShadowTables.Store4((sun.sunTable + id.x) * 16u, uint4(entry.page, entry.tag, entry.wanted, entry.view));
}
