// Keeps the clipmap's entries in step with its windows, a thread an entry: an entry whose level's window moved off the
// page it held lets that page go; a page is drawn again, all of it when the light turned or what depth is measured from
// moved, else where a sphere that moved reaches its column (the light is far, so a caster's shadow falls straight along
// it), border and all; a page wanted this frame is noted as read; and no page has a view until the allocator gives it
// one.

#include "Include/VirtualShadows.hlsli"

// The rectangle of a page the spheres that moved cover, each a square about where it is across the light, from the
// page's place in its level's window; none when they all miss it.
uint TouchedRect(GpuClipmap clipmap, GpuClipmapLevel level, int2 local, uint count)
{
    uint rect = 0u;
    float2 corner = (float2)local * level.pageSize;
    float perPage = (float)PageContent / ((float)PageTexels * level.pageSize);
    float border = (float)PageBorder / (float)PageTexels;
    [loop] for (uint i = 0u; i < count; i++)
    {
        float4 moved = LOAD(Moved, float4, i);
        float2 across = float2(dot(moved.xyz, clipmap.right), dot(moved.xyz, clipmap.up)) + level.camera - corner;
        float2 low = (across - moved.w) * perPage + border;
        float2 high = (across + moved.w) * perPage + border;
        rect = UnionRect(rect, RectOf(low, high, PageTexels));
    }

    return rect;
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    GpuClipmap clipmap = ClipmapOf();
    uint level = id.x / ClipmapEntriesPerLevel;
    if (level >= clipmap.levels)
    {
        return;
    }

    GpuClipmapLevel row = ClipmapLevelOf(level);
    ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.levelTable + id.x);
    int2 page = ClipmapPageAt(row, id.x);
    if (HasPage(entry.page))
    {
        uint record = RecordOf(clipmap, entry.page);
        bool isOwned = ShadowTables.Load(record * 16u) == id.x + 1u;
        if (entry.tag != ClipmapTagOf(page) || !isOwned)
        {
            if (isOwned)
            {
                ShadowTables.Store2(record * 16u, uint2(0u, 0u));
            }

            entry.page = 0u;
        }
        else
        {
            uint rect = clipmap.allDirty != 0u ? WholeRect : TouchedRect(clipmap, row, page - row.origin, clipmap.moved);
            if (rect != 0u)
            {
                entry.page |= PageDirty;
                ShadowTables.Store(record * 16u + 8u, UnionRect(ShadowTables.Load(record * 16u + 8u), rect));
            }

            if (entry.wanted == clipmap.frame)
            {
                ShadowTables.Store(record * 16u + 4u, clipmap.frame);
            }
        }
    }

    entry.view = NoView;
    ShadowTables.Store4((clipmap.levelTable + id.x) * 16u, uint4(entry.page, entry.tag, entry.wanted, entry.view));
}
