// Gives the pages wanted this frame the pool's pages, and picks which are drawn, in one group:
//
// 1. Every wanted entry with no page is counted by priority: each light's coarsest mip first, as its pixels fall back
//    on it and they are few; then the clipmap's levels, coarsest first, as a coarse page shadows four times the ground
//    the next finer does, so after a turn every pixel soon has some shadow, which then sharpens; then lights by
//    distance from the player, four buckets to a doubling. A light's page at its coarsest mip takes a small page, the
//    rest whole ones.
// 2. The pool's pages are listed, each kind apart: those nothing owns first, then those read longest ago, then the rest
//    no view read this frame; then the whole pages a view did read for a light, the farthest lights' first.
// 3. Each request takes the next listed page of its kind in priority order, unless a light that comes before it or
//    with it holds the page, so past the pages there are, the farthest lights are shadowed through their coarser mips;
//    a request with none to take gets none, and its pixels read the next coarser. The entries the pages were taken
//    from let go of them only once every request has taken its own, so none wanted this frame turns into a request
//    partway through.
// 4. Every wanted page that waits to be drawn (new, or something in it moved) is counted by the same priority, and the
//    first as many as the frame may draw (ShadowArgsViewRoom) become this frame's views: what takes a point onto the
//    page, its pool page, its entry's row, and the rectangle of it waiting, which its record lets go of; the rest wait.
//    Each clipmap page drawn and each light with a view is a group (ShadowGroups), which culling finds casters by: a
//    page with its view, a light with the faces and mips they are at and where it is.
// 5. The view's ShadowArgs: the pages' clear, a quad each, and the shadows' instance cull, a thread an instance; and in
//    ShadowCounts, how many pages are drawn and how many groups they are in.

#include "Include/Culling.hlsli"
#include "Include/VirtualShadows.hlsli"

// The classes requests and drawn pages are ordered by: lights' small pages, then the clipmap's levels, coarsest first,
// then lights' whole pages by distance.
static const uint Classes = 1u + ClipmapMaxLevels + Buckets;

// Frames since a page was read past which it is taken before those read since.
static const uint StaleFrames = 60u;

// A listed page is its index, with above it the priority of the light holding it, Classes for a page free to take,
// which every request comes before.
static const uint ClassShift = 24u;
static const uint CandidateIndex = (1u << ClassShift) - 1u;

groupshared uint Requested[2][Classes];
groupshared uint Taken[2][Classes];
groupshared uint Listed[2];
groupshared uint Held[Classes];
groupshared uint Drawn[Classes];
groupshared uint DrawnTaken[Classes];
groupshared uint Groups;
groupshared uint ViewRoom;

uint ClipmapClass(GpuClipmap clipmap, uint level)
{
    return clipmap.levels - level;
}

// 0 for a whole page, 1 for a small one.
uint KindOfKey(uint key)
{
    return MipOfKey(key) == LightMips - 1u ? 1u : 0u;
}

// A light's small page is first: its pixels read through it while their own pages wait, so a light whose small page
// waited would light them unshadowed. Its whole pages come after every clipmap level.
uint LightClass(uint key)
{
    return KindOfKey(key) == 1u ? 0u : 1u + ClipmapMaxLevels + BucketOf(WorldLightPosition(LOAD(WorldLights, GpuWorldLight, LightSlotOfKey(key))));
}

// Whether an entry is wanted this frame and has a page waiting to be drawn.
bool IsWaiting(ShadowEntry entry, uint frame)
{
    return entry.wanted == frame && HasPage(entry.page) && (entry.page & PageDirty) != 0u;
}

// The priority of the light whose entry in this frame's table holds a page a view read this frame, which a request
// nearer than it may take, the light falling back on its coarser mips; 0, which no request comes before, for any other
// page, and for the clipmap's, whose coarser levels are what nearer pixels' soft shadows are filtered through.
uint HeldClass(GpuClipmap clipmap, uint2 record)
{
    if ((record.x & LightOwner) == 0u || record.y != clipmap.frame)
    {
        return 0u;
    }

    return LightClass(ShadowTables.Load((clipmap.lightTable + (record.x & ~LightOwner)) * 16u + 4u));
}

// Takes a pool page from the entry that last had it, the clipmap's or a light's in this frame's table, when it still
// does.
void TakeFrom(GpuClipmap clipmap, uint owner, uint page)
{
    uint row = (owner & LightOwner) != 0u ? clipmap.lightTable + (owner & ~LightOwner) : clipmap.levelTable + owner - 1u;
    if ((ShadowTables.Load(row * 16u) & ~PageDirty) == page)
    {
        ShadowTables.Store(row * 16u, 0u);
    }
}

// The rectangle of a page waiting to be drawn, which its record lets go of as it is drawn; all of it should none be
// noted.
uint TakeRect(GpuClipmap clipmap, uint page)
{
    uint record = RecordOf(clipmap, page);
    uint rect = ShadowTables.Load(record * 16u + 8u);
    ShadowTables.Store(record * 16u + 8u, 0u);
    return rect != 0u ? rect : WholeRect;
}

// What takes a camera-relative point onto a clipmap page as clip, its 128 texels across -1 to 1, depth reverse-Z.
GpuShadowView ClipmapView(GpuClipmap clipmap, GpuClipmapLevel row, uint level, int2 page)
{
    float3 offset = CameraPosition - clipmap.camera;
    float content = (float)PageContent / (float)PageTexels;
    float2 local = (float2)(page - row.origin);
    float scale = 2.0 * content / row.pageSize;
    GpuShadowView view;
    view.row0 = float4(clipmap.right * scale, content * (2.0 * (dot(offset, clipmap.right) + row.camera.x) / row.pageSize - 2.0 * local.x - 1.0));
    view.row1 = float4(-clipmap.up * scale, content * (1.0 - 2.0 * (dot(offset, clipmap.up) + row.camera.y) / row.pageSize + 2.0 * local.y));
    view.row2 = float4(-clipmap.direction / (2.0 * clipmap.depthRange), 0.5 - (dot(offset, clipmap.direction) + clipmap.cameraDepth) / (2.0 * clipmap.depthRange));
    view.row3 = float4(0.0, 0.0, 0.0, 1.0);
    view.page = 0u;
    view.group = level;
    view.rect = WholeRect;
    view.entry = 0u;
    return view;
}

// What takes a camera-relative point onto a page of a light's face as clip: its face's perspective from the light, the
// page's part of it across -1 to 1, depth LightNear over the distance ahead.
GpuShadowView LightView(uint key)
{
    uint slot = LightSlotOfKey(key);
    float3 light = WorldLightPosition(LOAD(WorldLights, GpuWorldLight, slot));
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
    view.entry = 0u;
    return view;
}

[numthreads(1024, 1, 1)]
void main(uint thread : SV_GroupIndex)
{
    GpuClipmap clipmap = ClipmapOf();
    uint clipmapEntries = clipmap.levels * ClipmapEntriesPerLevel;
    uint lightEntries = clipmap.lightEntries;
    uint wholePages = clipmap.wholePages;
    uint records = clipmap.poolAcross * clipmap.poolAcross;
    uint views = CountOf(ShadowViews, 80u);
    for (uint clear = thread; clear < Classes; clear += 1024u)
    {
        Requested[0][clear] = 0u;
        Requested[1][clear] = 0u;
        Taken[0][clear] = 0u;
        Taken[1][clear] = 0u;
        Held[clear] = 0u;
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
    for (uint e = thread; e < clipmapEntries; e += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.levelTable + e);
        if (entry.wanted == clipmap.frame && !HasPage(entry.page))
        {
            InterlockedAdd(Requested[0][ClipmapClass(clipmap, e / ClipmapEntriesPerLevel)], 1u);
        }
    }

    for (uint l = thread; l < lightEntries; l += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.lightTable + l);
        if (entry.tag != EmptyKey && entry.wanted == clipmap.frame && !HasPage(entry.page))
        {
            InterlockedAdd(Requested[KindOfKey(entry.tag)][LightClass(entry.tag)], 1u);
        }
    }

    // 2. The pages free to take, nobody's first, then those read longest ago, then the rest not read this frame.
    [unroll] for (uint round = 0u; round < 3u; round++)
    {
        for (uint p = thread; p < wholePages + clipmap.smallPages; p += 1024u)
        {
            uint kind = p < wholePages ? 0u : 1u;
            uint index = kind == 0u ? p : p - wholePages;
            uint2 record = ShadowTables.Load2((kind == 0u ? index : records + index) * 16u);
            uint age = clipmap.frame - record.y;
            bool isListed = round == 0u ? record.x == 0u
                : round == 1u ? record.x != 0u && age > StaleFrames
                : record.x != 0u && age > 0u && age <= StaleFrames;
            if (isListed)
            {
                uint at;
                InterlockedAdd(Listed[kind], 1u, at);
                ShadowCandidates.Store((kind == 0u ? at : wholePages + at) * 4u, index | (Classes << ClassShift));
            }
        }

        DeviceMemoryBarrierWithGroupSync();
    }

    // Then the whole pages a view read this frame for a light, by the light's priority, the farthest first.
    for (uint hc = thread; hc < wholePages; hc += 1024u)
    {
        uint klass = HeldClass(clipmap, ShadowTables.Load2(hc * 16u));
        if (klass > 0u)
        {
            InterlockedAdd(Held[klass], 1u);
        }
    }

    GroupMemoryBarrierWithGroupSync();
    if (thread == 0u)
    {
        uint at = Listed[0];
        for (uint c = Classes - 1u; c > 0u; c--)
        {
            uint count = Held[c];
            Held[c] = at;
            at += count;
        }

        Listed[0] = at;
    }

    GroupMemoryBarrierWithGroupSync();
    for (uint hp = thread; hp < wholePages; hp += 1024u)
    {
        uint klass = HeldClass(clipmap, ShadowTables.Load2(hp * 16u));
        if (klass > 0u)
        {
            uint at;
            InterlockedAdd(Held[klass], 1u, at);
            ShadowCandidates.Store(at * 4u, hp | (klass << ClassShift));
        }
    }

    DeviceMemoryBarrierWithGroupSync();

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

    // 3. Each request takes the next page of its kind, in priority order, unless a light that comes before it or with
    //    it holds it; the page's record keeps what owned it, which lets go of it below.
    for (uint s = thread; s < clipmapEntries; s += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.levelTable + s);
        if (entry.wanted != clipmap.frame || HasPage(entry.page))
        {
            continue;
        }

        uint level = s / ClipmapEntriesPerLevel;
        uint klass = ClipmapClass(clipmap, level);
        uint rank;
        InterlockedAdd(Taken[0][klass], 1u, rank);
        rank += Requested[0][klass];
        uint candidate = rank < Listed[0] ? ShadowCandidates.Load(rank * 4u) : 0u;
        if (candidate >> ClassShift <= klass)
        {
            continue;
        }

        uint index = candidate & CandidateIndex;
        ShadowTables.Store2((clipmap.levelTable + s) * 16u, uint2((index + 1u) | PageDirty, ClipmapTagOf(ClipmapPageAt(ClipmapLevelOf(level), s))));
        ShadowTables.Store4(index * 16u, uint4(s + 1u, clipmap.frame, WholeRect, ShadowTables.Load(index * 16u)));
    }

    for (uint t = thread; t < lightEntries; t += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.lightTable + t);
        if (entry.tag == EmptyKey || entry.wanted != clipmap.frame || HasPage(entry.page))
        {
            continue;
        }

        uint kind = KindOfKey(entry.tag);
        uint klass = LightClass(entry.tag);
        uint rank;
        InterlockedAdd(Taken[kind][klass], 1u, rank);
        rank += Requested[kind][klass];
        uint candidate = rank < Listed[kind] ? ShadowCandidates.Load((kind == 0u ? rank : wholePages + rank) * 4u) : 0u;
        if (candidate >> ClassShift <= klass)
        {
            continue;
        }

        uint page = ((candidate & CandidateIndex) + 1u) | (kind == 1u ? PageSmall : 0u);
        uint record = RecordOf(clipmap, page);
        ShadowTables.Store((clipmap.lightTable + t) * 16u, page | PageDirty);
        ShadowTables.Store4(record * 16u, uint4(t | LightOwner, clipmap.frame, WholeRect, ShadowTables.Load(record * 16u)));
    }

    DeviceMemoryBarrierWithGroupSync();

    // The entries the pages were taken from let go of them, when they have not since been given them again.
    for (uint g = thread; g < wholePages + clipmap.smallPages; g += 1024u)
    {
        uint kind = g < wholePages ? 0u : 1u;
        uint index = kind == 0u ? g : g - wholePages;
        uint record = kind == 0u ? index : records + index;
        uint4 row = ShadowTables.Load4(record * 16u);
        if (row.w != 0u)
        {
            if (row.w != row.x)
            {
                TakeFrom(clipmap, row.w, (index + 1u) | (kind == 1u ? PageSmall : 0u));
            }

            ShadowTables.Store(record * 16u + 12u, 0u);
        }
    }

    DeviceMemoryBarrierWithGroupSync();

    // 4. The pages drawn this frame: those waiting, by priority, as many as the frame may draw. A light with one
    //    waiting starts the frame with no faces or mips noted.
    for (uint ds = thread; ds < clipmapEntries; ds += 1024u)
    {
        if (IsWaiting(TABLE_ENTRY(ShadowTables, clipmap.levelTable + ds), clipmap.frame))
        {
            InterlockedAdd(Drawn[ClipmapClass(clipmap, ds / ClipmapEntriesPerLevel)], 1u);
        }
    }

    for (uint dl = thread; dl < lightEntries; dl += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.lightTable + dl);
        if (entry.tag != EmptyKey && IsWaiting(entry, clipmap.frame))
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
    for (uint vs = thread; vs < clipmapEntries; vs += 1024u)
    {
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.levelTable + vs);
        if (!IsWaiting(entry, clipmap.frame))
        {
            continue;
        }

        uint level = vs / ClipmapEntriesPerLevel;
        uint klass = ClipmapClass(clipmap, level);
        uint rank;
        InterlockedAdd(DrawnTaken[klass], 1u, rank);
        rank += Drawn[klass];
        if (rank >= ViewRoom)
        {
            continue;
        }

        GpuClipmapLevel row = ClipmapLevelOf(level);
        int2 page = ClipmapPageAt(row, vs);
        GpuShadowView view = ClipmapView(clipmap, row, level, page);
        view.page = entry.page & ~PageDirty;
        view.rect = TakeRect(clipmap, view.page);
        view.entry = clipmap.levelTable + vs;
        STORE(ShadowViews, GpuShadowView, rank, view);
        ShadowTables.Store((clipmap.levelTable + vs) * 16u, view.page);
        ShadowTables.Store((clipmap.levelTable + vs) * 16u + 12u, rank);
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
        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.lightTable + vl);
        if (entry.tag == EmptyKey || !IsWaiting(entry, clipmap.frame))
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
        view.rect = TakeRect(clipmap, view.page);
        view.entry = clipmap.lightTable + vl;
        STORE(ShadowViews, GpuShadowView, rank, view);
        ShadowTables.Store((clipmap.lightTable + vl) * 16u, view.page);
        ShadowTables.Store((clipmap.lightTable + vl) * 16u + 12u, rank);
        uint slot = LightSlotOfKey(entry.tag);
        uint faceMip = ((entry.tag >> 9) & 7u) * LightMips + MipOfKey(entry.tag);
        ShadowLightGroups.InterlockedOr(slot * 8u + 4u, 1u << faceMip);
        ShadowLightGroups.Store(slot * 8u, clipmap.frame);
    }

    DeviceMemoryBarrierWithGroupSync();

    // The lights' groups, after the clipmap pages', each with its faces and mips drawn, where it is and how far it
    // reaches.
    uint groupRoom = CountOf(ShadowGroups, 32u);
    uint lightSlots = CountOf(ShadowLightGroups, 8u);
    for (uint slot = thread; slot < lightSlots; slot += 1024u)
    {
        if (ShadowLightGroups.Load(slot * 8u) != clipmap.frame)
        {
            continue;
        }

        GpuWorldLight light = LOAD(WorldLights, GpuWorldLight, slot);
        GpuShadowGroup group;
        group.target = slot | LightOwner;
        group.faceMips = ShadowLightGroups.Load(slot * 8u + 4u);
        group.texel = 0.0;
        group.view = NoView;
        group.light = WorldLightPosition(light);
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
        uint instanceGroups = (CountOf(Instances, GpuInstanceBytes) + 63u) / 64u;
        ShadowCounts.Store(ShadowCountGroups, groups);
        ShadowArgs.Store3(ShadowArgsInstanceCull, uint3(groups > 0u ? clamp(instanceGroups, 1u, MaxGroupsX) : 0u, 1u, 1u));
        ShadowArgs.Store4(ShadowArgsClear, uint4(6u, ShadowCounts.Load(ShadowCountViews), 0u, 0u));
        ShadowArgs.Store(ShadowArgsClear + 16u, 0u);
    }
}
