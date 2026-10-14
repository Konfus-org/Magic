// Gives each light's page wanted this frame the pool page it had last frame, a thread an entry of this frame's table:
// found by its key in last frame's table, still owned by it, it is taken over, and drawn again, all of it when its
// light changed, else where something that moved within its reach covers it, since, and where last frame drew it short
// of casters. What was not wanted again is left in the pool, the first taken for what is wanted now.

#include "Include/VirtualShadows.hlsli"

// The rectangle of the page a key names that the spheres that moved within range of its light, at a point relative to
// the main camera, cover; none when they all miss it.
uint TouchedRect(float3 light, float range, uint key, uint count)
{
    uint rect = 0u;
    [loop] for (uint i = 0u; i < count; i++)
    {
        float4 moved = LOAD(Moved, float4, i);
        float3 apart = moved.xyz - light;
        float reach = range + moved.w;
        if (dot(apart, apart) <= reach * reach)
        {
            rect = UnionRect(rect, LightPageRectOf(key, apart, moved.w));
        }
    }

    return rect;
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    GpuSunClipmap sun = SunClipmapOf();
    if (id.x >= sun.lightEntries)
    {
        return;
    }

    ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.lightTable + id.x);
    if (entry.tag == EmptyKey || entry.wanted != sun.frame)
    {
        return;
    }

    uint slot = LightSlotOfKey(entry.tag);
    GpuSceneLight light = LOAD(SceneLights, GpuSceneLight, slot);
    if ((light.flags & SceneLightLive) == 0u || (light.flags & SceneLightShadowless) != 0u)
    {
        return;
    }

    uint last = FindLightEntry(ShadowTables, sun.lightTableLast, entry.tag, sun.lightEntries);
    if (last == NoView)
    {
        return;
    }

    uint page = ShadowTables.Load((sun.lightTableLast + last) * 16u);
    if (!HasPage(page))
    {
        return;
    }

    uint record = RecordOf(sun, page);
    if (ShadowTables.Load(record * 16u) != (last | LightOwner))
    {
        return;
    }

    bool hasChanged = slot / 32u < sun.changedLightWords && (ChangedLights.Load((slot / 32u) * 4u) & (1u << (slot % 32u))) != 0u;
    uint shortRect = ShortRect(ShadowTables.Load((sun.lightTableLast + last) * 16u + 12u));
    uint rect = hasChanged ? WholeRect : UnionRect(TouchedRect(MainRelative(sun, SceneLightPosition(light)), light.range, entry.tag, sun.moved), shortRect);
    ShadowTables.Store((sun.lightTable + id.x) * 16u, (page & ~PageDirty) | (rect != 0u || (page & PageDirty) != 0u ? PageDirty : 0u));
    ShadowTables.Store2(record * 16u, uint2(id.x | LightOwner, sun.frame));
    if (rect != 0u)
    {
        ShadowTables.Store(record * 16u + 8u, UnionRect(ShadowTables.Load(record * 16u + 8u), rect));
    }
}
