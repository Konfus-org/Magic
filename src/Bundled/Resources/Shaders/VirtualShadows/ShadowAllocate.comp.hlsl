// Gives the pages wanted this frame the pool's pages, and picks which are drawn, in one group:
//
// 1. Every wanted entry with no page is counted by priority: the sun's four coarsest levels first, as every pixel falls
//    back on them and they are few, then by distance from the player, four buckets to a doubling, a sun level by how
//    far the pixels that want it are, a light by where it is. A light's page at its coarsest mip takes a small page,
//    the rest whole ones.
// 2. The pool's pages no view read this frame are listed, each kind apart: those nothing owns first, then those read
//    longest ago, then the rest.
// 3. Each request takes the next listed page of its kind in priority order, the page's last owner, when a sun entry,
//    losing it; a request past the pages there are gets none, and its pixels read the next coarser.
// 4. Every wanted page that waits to be drawn (new, or something in it moved) is counted by the same priority, and the
//    first as many as the frame may draw (ShadowArgsViewRoom) become this frame's views: what takes a point onto the
//    page, its pool page, and the rectangle of it waiting, which its record lets go of; the rest wait. Each sun page
//    drawn and each light with a view is a group (ShadowGroups), which culling finds casters by: a page with its view,
//    a light with the faces and mips they are at and where it is.
// 5. The view's ShadowArgs: the pages' clear, a quad each, and the shadows' instance cull, a thread an instance; and in
//    ShadowCounts, how many pages are drawn and how many groups they are in.

#include "Include/Culling.hlsli"
#include "Include/VirtualShadows.hlsli"

static const uint Classes = Buckets + 1u;

// Frames since a page was read past which it is taken before those read since.
static const uint StaleFrames = 60u;

groupshared uint Requested[2][Classes];
groupshared uint Taken[2][Classes];
groupshared uint Listed[2];
groupshared uint Drawn[Classes];
groupshared uint DrawnTaken[Classes];
groupshared uint Groups;
groupshared uint ViewRoom;

uint SunClass(GpuSunClipmap sun, uint level)
{
    return level + 4u >= sun.levels ? 0u : 1u + BucketOfDistance(SunReachOf(sun, level));
}

uint LightClass(uint key)
{
    return 1u + BucketOf(SceneLightPosition(LOAD(SceneLights, GpuSceneLight, LightSlotOfKey(key))));
}

// 0 for a whole page, 1 for a small one.
uint KindOfKey(uint key)
{
    return MipOfKey(key) == LightMips - 1u ? 1u : 0u;
}

// Whether an entry is wanted this frame and has a page waiting to be drawn.
bool IsWaiting(ShadowEntry entry, uint frame)
{
    return entry.wanted == frame && HasPage(entry.page) && (entry.page & PageDirty) != 0u;
}

// Takes a pool page from the sun entry that last had it, when it still does.
void TakeFromSun(GpuSunClipmap sun, uint record, uint page)
{
    uint owner = ShadowTables.Load(record * 16u);
    if (owner == 0u || (owner & LightOwner) != 0u)
    {
        return;
    }

    uint row = sun.sunTable + owner - 1u;
    if ((ShadowTables.Load(row * 16u) & ~PageDirty) == page)
    {
        ShadowTables.Store(row * 16u, 0u);
    }
}

// The rectangle of a page waiting to be drawn, which its record lets go of as it is drawn; all of it should none be
// noted.
uint TakeRect(GpuSunClipmap sun, uint page)
{
    uint record = RecordOf(sun, page);
    uint rect = ShadowTables.Load(record * 16u + 8u);
    ShadowTables.Store(record * 16u + 8u, 0u);
    return rect != 0u ? rect : WholeRect;
}

// What takes a camera-relative point onto a sun page as clip, its 128 texels across -1 to 1, depth reverse-Z.
GpuShadowView SunView(GpuSunClipmap sun, GpuSunLevel row, uint level, int2 page)
{
    float3 offset = (CameraPosition - sun.cameraHigh) + (CameraLow - sun.cameraLow);
    float content = (float)PageContent / (float)PageTexels;
    float2 local = (float2)(page - row.origin);
    float scale = 2.0 * content / row.pageSize;
    GpuShadowView view;
    view.row0 = float4(sun.right * scale, content * (2.0 * (dot(offset, sun.right) + row.camera.x) / row.pageSize - 2.0 * local.x - 1.0));
    view.row1 = float4(-sun.up * scale, content * (1.0 - 2.0 * (dot(offset, sun.up) + row.camera.y) / row.pageSize + 2.0 * local.y));
    view.row2 = float4(-sun.direction / (2.0 * sun.depthRange), 0.5 - (dot(offset, sun.direction) + sun.cameraDepth) / (2.0 * sun.depthRange));
    view.row3 = float4(0.0, 0.0, 0.0, 1.0);
    view.page = 0u;
    view.group = level;
    view.rect = WholeRect;
    view.padding = 0u;
    return view;
}

// What takes a camera-relative point onto a page of a light's face as clip: its face's perspective from the light, the
// page's part of it across -1 to 1, depth LightNear over the distance ahead.
GpuShadowView LightView(uint key)
{
    uint slot = LightSlotOfKey(key);
    float3 light = SceneLightPosition(LOAD(SceneLights, GpuSceneLight, slot));
    float3 across;
    float3 upward;
    float3 forward;
    LightPageRows(key, across, upward, forward);
    GpuShadowView view;
    view.row0 = float4(across, -dot(across, light));
    view.row1 = float4(upward, -dot(upward, light));
    view.row2 = float4(0.0, 0.0, 0.0, LightNear);
    view.row3 = float4(forward, -dot(forward, light));
    view.page = 0u;
    view.group = slot | LightOwner;
    view.rect = WholeRect;
    view.padding = 0u;
    return view;
}

[numthreads(1024, 1, 1)]
void main(uint thread : SV_GroupIndex)
{
    GpuSunClipmap sun = SunClipmapOf();
    uint sunEntries = sun.levels * SunEntriesPerLevel;
    uint lightEntries = sun.lightEntries;
    uint wholePages = sun.wholePages;
    uint records = sun.poolAcross * sun.poolAcross;
    uint views = CountOf(ShadowViews, 80u);
    for (uint clear = thread; clear < Classes; clear += 1024u)
    {
        Requested[0][clear] = 0u;
        Requested[1][clear] = 0u;
        Taken[0][clear] = 0u;
        Taken[1][clear] = 0u;
        Drawn[clear] = 0u;
        DrawnTaken[clear] = 0u;
    }

    if (thread == 0u)
    {
        Listed[0] = 0u;
        Listed[1] = 0u;
        Groups = 0u;
        uint room = ShadowArgs.Load(ShadowArgsViewRoom);
        room = room == 0u || room > views ? views : room;
        room = ShadowArgs.Load(ShadowArgsOverflowed) == 1u ? max(room / 2u, 1u) : min(room * 2u, views);
        ShadowArgs.Store(ShadowArgsViewRoom, room);
        ViewRoom = room;
    }

    GroupMemoryBarrierWithGroupSync();

    // 1. The requests, counted by priority.
    for (uint e = thread; e < sunEntries; e += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.sunTable + e);
        if (entry.wanted == sun.frame && !HasPage(entry.page))
        {
            InterlockedAdd(Requested[0][SunClass(sun, e / SunEntriesPerLevel)], 1u);
        }
    }

    for (uint l = thread; l < lightEntries; l += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.lightTable + l);
        if (entry.tag != EmptyKey && entry.wanted == sun.frame && !HasPage(entry.page))
        {
            InterlockedAdd(Requested[KindOfKey(entry.tag)][LightClass(entry.tag)], 1u);
        }
    }

    // 2. The pages free to take, nobody's first, then those read longest ago, then the rest not read this frame.
    [unroll] for (uint round = 0u; round < 3u; round++)
    {
        for (uint p = thread; p < wholePages + sun.smallPages; p += 1024u)
        {
            uint kind = p < wholePages ? 0u : 1u;
            uint index = kind == 0u ? p : p - wholePages;
            uint2 record = ShadowTables.Load2((kind == 0u ? index : records + index) * 16u);
            uint age = sun.frame - record.y;
            bool isListed = round == 0u ? record.x == 0u
                : round == 1u ? record.x != 0u && age > StaleFrames
                : record.x != 0u && age > 0u && age <= StaleFrames;
            if (isListed)
            {
                uint at;
                InterlockedAdd(Listed[kind], 1u, at);
                ShadowCandidates.Store((kind == 0u ? at : wholePages + at) * 4u, index);
            }
        }

        DeviceMemoryBarrierWithGroupSync();
    }

    // Each class's first place in its kind's order.
    if (thread < 2u)
    {
        uint total = 0u;
        for (uint c = 0u; c < Classes; c++)
        {
            uint count = Requested[thread][c];
            Requested[thread][c] = total;
            total += count;
        }
    }

    GroupMemoryBarrierWithGroupSync();

    // 3. Each request takes the next page of its kind, in priority order.
    for (uint s = thread; s < sunEntries; s += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.sunTable + s);
        if (entry.wanted != sun.frame || HasPage(entry.page))
        {
            continue;
        }

        uint level = s / SunEntriesPerLevel;
        uint klass = SunClass(sun, level);
        uint rank;
        InterlockedAdd(Taken[0][klass], 1u, rank);
        rank += Requested[0][klass];
        if (rank >= Listed[0])
        {
            continue;
        }

        uint index = ShadowCandidates.Load(rank * 4u);
        TakeFromSun(sun, index, index + 1u);
        ShadowTables.Store2((sun.sunTable + s) * 16u, uint2((index + 1u) | PageDirty, SunTagOf(SunPageAt(SunLevelOf(level), s))));
        ShadowTables.Store3(index * 16u, uint3(s + 1u, sun.frame, WholeRect));
    }

    for (uint t = thread; t < lightEntries; t += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.lightTable + t);
        if (entry.tag == EmptyKey || entry.wanted != sun.frame || HasPage(entry.page))
        {
            continue;
        }

        uint kind = KindOfKey(entry.tag);
        uint klass = LightClass(entry.tag);
        uint rank;
        InterlockedAdd(Taken[kind][klass], 1u, rank);
        rank += Requested[kind][klass];
        if (rank >= Listed[kind])
        {
            continue;
        }

        uint index = ShadowCandidates.Load((kind == 0u ? rank : wholePages + rank) * 4u);
        uint page = (index + 1u) | (kind == 1u ? PageSmall : 0u);
        uint record = RecordOf(sun, page);
        if (kind == 0u)
        {
            TakeFromSun(sun, record, page);
        }

        ShadowTables.Store((sun.lightTable + t) * 16u, page | PageDirty);
        ShadowTables.Store3(record * 16u, uint3(t | LightOwner, sun.frame, WholeRect));
    }

    DeviceMemoryBarrierWithGroupSync();

    // 4. The pages drawn this frame: those waiting, by priority, as many as the frame may draw. A light with one
    //    waiting starts the frame with no faces or mips noted.
    for (uint ds = thread; ds < sunEntries; ds += 1024u)
    {
        if (IsWaiting(TABLE_ENTRY(ShadowTables, sun.sunTable + ds), sun.frame))
        {
            InterlockedAdd(Drawn[SunClass(sun, ds / SunEntriesPerLevel)], 1u);
        }
    }

    for (uint dl = thread; dl < lightEntries; dl += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.lightTable + dl);
        if (entry.tag != EmptyKey && IsWaiting(entry, sun.frame))
        {
            InterlockedAdd(Drawn[LightClass(entry.tag)], 1u);
            ShadowLightGroups.Store(LightSlotOfKey(entry.tag) * 8u + 4u, 0u);
        }
    }

    DeviceMemoryBarrierWithGroupSync();
    if (thread == 0u)
    {
        uint total = 0u;
        for (uint c = 0u; c < Classes; c++)
        {
            uint count = Drawn[c];
            Drawn[c] = total;
            total += count;
        }

        ShadowCounts.Store(ShadowCountViews, min(total, ViewRoom));
    }

    GroupMemoryBarrierWithGroupSync();
    for (uint vs = thread; vs < sunEntries; vs += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.sunTable + vs);
        if (!IsWaiting(entry, sun.frame))
        {
            continue;
        }

        uint level = vs / SunEntriesPerLevel;
        uint klass = SunClass(sun, level);
        uint rank;
        InterlockedAdd(DrawnTaken[klass], 1u, rank);
        rank += Drawn[klass];
        if (rank >= ViewRoom)
        {
            continue;
        }

        GpuSunLevel row = SunLevelOf(level);
        int2 page = SunPageAt(row, vs);
        GpuShadowView view = SunView(sun, row, level, page);
        view.page = entry.page & ~PageDirty;
        view.rect = TakeRect(sun, view.page);
        STORE(ShadowViews, GpuShadowView, rank, view);
        ShadowTables.Store((sun.sunTable + vs) * 16u, view.page);
        ShadowTables.Store((sun.sunTable + vs) * 16u + 12u, rank);
        GpuShadowGroup group;
        group.target = level;
        group.faceMips = 0u;
        group.texel = row.texel;
        group.view = rank;
        group.light = float3(0.0, 0.0, 0.0);
        group.range = 0.0;
        uint at;
        InterlockedAdd(Groups, 1u, at);
        if (at < CountOf(ShadowGroups, 32u))
        {
            STORE(ShadowGroups, GpuShadowGroup, at, group);
        }
    }

    for (uint vl = thread; vl < lightEntries; vl += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.lightTable + vl);
        if (entry.tag == EmptyKey || !IsWaiting(entry, sun.frame))
        {
            continue;
        }

        uint klass = LightClass(entry.tag);
        uint rank;
        InterlockedAdd(DrawnTaken[klass], 1u, rank);
        rank += Drawn[klass];
        if (rank >= ViewRoom)
        {
            continue;
        }

        GpuShadowView view = LightView(entry.tag);
        view.page = entry.page & ~PageDirty;
        view.rect = TakeRect(sun, view.page);
        STORE(ShadowViews, GpuShadowView, rank, view);
        ShadowTables.Store((sun.lightTable + vl) * 16u, view.page);
        ShadowTables.Store((sun.lightTable + vl) * 16u + 12u, rank);
        uint slot = LightSlotOfKey(entry.tag);
        uint faceMip = ((entry.tag >> 9) & 7u) * LightMips + MipOfKey(entry.tag);
        ShadowLightGroups.InterlockedOr(slot * 8u + 4u, 1u << faceMip);
        ShadowLightGroups.Store(slot * 8u, sun.frame);
    }

    DeviceMemoryBarrierWithGroupSync();

    // The lights' groups, after the sun pages', each with its faces and mips drawn, where it is and how far it reaches.
    uint groupRoom = CountOf(ShadowGroups, 32u);
    uint lightSlots = CountOf(ShadowLightGroups, 8u);
    for (uint slot = thread; slot < lightSlots; slot += 1024u)
    {
        if (ShadowLightGroups.Load(slot * 8u) != sun.frame)
        {
            continue;
        }

        GpuSceneLight light = LOAD(SceneLights, GpuSceneLight, slot);
        GpuShadowGroup group;
        group.target = slot | LightOwner;
        group.faceMips = ShadowLightGroups.Load(slot * 8u + 4u);
        group.texel = 0.0;
        group.view = NoView;
        group.light = SceneLightPosition(light);
        group.range = light.range;
        uint at;
        InterlockedAdd(Groups, 1u, at);
        if (at < groupRoom)
        {
            STORE(ShadowGroups, GpuShadowGroup, at, group);
        }
    }

    // 5. The clear and the instance cull.
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0u)
    {
        uint groups = min(Groups, groupRoom);
        uint instanceGroups = (CountOf(Instances, 160u) + 63u) / 64u;
        ShadowCounts.Store(ShadowCountGroups, groups);
        ShadowArgs.Store3(ShadowArgsInstanceCull, uint3(groups > 0u ? clamp(instanceGroups, 1u, MaxGroupsX) : 0u, 1u, 1u));
        ShadowArgs.Store4(ShadowArgsClear, uint4(6u, ShadowCounts.Load(ShadowCountViews), 0u, 0u));
        ShadowArgs.Store(ShadowArgsClear + 16u, 0u);
    }
}
