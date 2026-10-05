// Gives this frame's shadow-casting spot and point lights their pages of the atlas, on the GPU: one group. From
// the candidates the candidates pass listed (every light that may cast, with its score: how much of the main
// view it could reach), the best maxLocalLights are chosen by that many rounds of a parallel arg-max,
// and each chosen light keeps the slot it held last frame when its key (its place and reach, quantised) still
// matches, else takes a free one: a light's pages are drawn again only when it is new or when its turn comes.
// The faces drawn this frame are a new light's, all at once, then the oldest, within facesPerFrame, and never
// more rows than the draw args and visible-id buffers have slices for (visibleSlots, matching the creates in
// the seed pass's file and the draw pass's iterations): a refreshed row's slice is its place in the refreshed
// list, cascades first.
//
// Writes the face rows of every slot in use (their matrices from the light as it is now), each light's shadow
// row and face count into the lights buffer, the slots' state, the rest of the refreshed list after the
// cascades, and the cull's indirect dispatch: one group per page per refreshed row (Cull/CullShadow.comp).
// Twin, kept as the reference: Reference/LocalShadows.cs.

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"

#define THREADS 64

struct PassParams
{
    uint maxLocalLights = 8;
    uint facesPerFrame = 4;
    uint visibleSlots = 10; // slices of the draw args and visible ids: the same number as their creates' slots
};

StructuredBuffer<GpuCounts> Counts : READ(0);
StructuredBuffer<uint> ShadowCandidates : READ(1); // [0] the count, then pairs: the light's row, its score's bits
RWStructuredBuffer<GpuLight> Lights : WRITE(0);
RWStructuredBuffer<GpuShadowView> ShadowViews : WRITE(1);
RWStructuredBuffer<GpuShadowHeader> ShadowHeader : WRITE(2);
RWStructuredBuffer<GpuLocalShadowSlot> ShadowSlots : WRITE(3);
RWStructuredBuffer<uint> ShadowRefreshed : WRITE(4);
RWStructuredBuffer<uint> ShadowCullArgs : WRITE(5);

groupshared float BestScore[THREADS];
groupshared uint BestLight[THREADS];
groupshared uint Chosen[ShadowMaxSlots];
groupshared uint ChosenCount;

static const uint MaxCandidates = 1023u;

bool IsChosen(uint lightIndex, uint count)
{
    bool chosen = false;
    [loop] for (uint i = 0u; i < count; i++)
        chosen = chosen || Chosen[i] == lightIndex;
    return chosen;
}

// One face's row from the light as it is now.
GpuShadowView FaceRow(GpuLight light, uint face, uint faces, uint page, GpuShadowHeader header, bool refresh, uint slice)
{
    bool isPoint = faces > 1u;
    float fov = isPoint
        ? radians(ShadowPointFaceFovDegrees)
        : radians(clamp(degrees(2.0 * acos(clamp(light.directionOuterCos.w, -1.0, 1.0))) + ShadowSpotMarginDegrees, 1.0, 170.0));
    float tanHalf = tan(fov * 0.5);
    float far = max(light.positionRange.w, ShadowLocalNear + 0.01);
    float4x4 rotation = isPoint ? LightRotation(ShadowFaceForward(face)) : LightRotation(light.directionOuterCos.xyz);
    float4 tileTexels = PageTexels(page, header.layout.w, header.layout.z, header.layout.x);

    GpuShadowView row;
    row.viewProj = mul(PerspectiveReverseZ(tanHalf, ShadowLocalNear, far), rotation);
    row.rotation = rotation;
    row.eye = float4(light.positionRange.xyz, 2.0 * tanHalf / tileTexels.z);
    row.tileUv = TexelsToUv(tileTexels, float2(header.layout.xy));
    row.tileTexels = tileTexels;
    row.range = float4(light.positionRange.w, ShadowLocalNear, far, tanHalf);
    row.flags = uint4(ShadowViewUsed, refresh ? 1u : 0u, 0u, slice);
    [unroll] for (uint i = 0u; i < 12u; i++)
        row.planes[i] = float4(0.0, 0.0, 0.0, 0.0);
    return row;
}

[numthreads(THREADS, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    PassParams passParams = LoadPassParams();
    uint tid = threadId.x;
    GpuCounts counts = Counts[0];
    GpuShadowHeader header = ShadowHeader[0];
    uint slotCount = min(min(passParams.maxLocalLights, header.slots.x), ShadowMaxSlots);
    uint candidates = min(ShadowCandidates[0], MaxCandidates);

    if (tid == 0u)
        ChosenCount = 0u;
    GroupMemoryBarrierWithGroupSync();

    // The best slotCount lights, one per round: each thread's best over its share, then the group's best of those.
    [loop] for (uint round = 0u; round < slotCount; round++)
    {
        float best = 0.0;
        uint bestLight = ShadowNone;
        uint chosenSoFar = ChosenCount;
        [loop] for (uint c = tid; c < candidates; c += THREADS)
        {
            uint lightIndex = ShadowCandidates[1u + c * 2u];
            float score = asfloat(ShadowCandidates[2u + c * 2u]);
            [flatten] if (score > best && !IsChosen(lightIndex, chosenSoFar))
            {
                best = score;
                bestLight = lightIndex;
            }
        }

        BestScore[tid] = best;
        BestLight[tid] = bestLight;
        GroupMemoryBarrierWithGroupSync();

        [branch] if (tid == 0u)
        {
            float top = 0.0;
            uint topLight = ShadowNone;
            [loop] for (uint t = 0u; t < THREADS; t++)
            {
                [flatten] if (BestLight[t] != ShadowNone && BestScore[t] > top)
                {
                    top = BestScore[t];
                    topLight = BestLight[t];
                }
            }

            [flatten] if (topLight != ShadowNone)
            {
                Chosen[ChosenCount] = topLight;
                ChosenCount++;
            }
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (tid != 0u)
        return;

    // From here one thread: a handful of slots and lights.
    uint chosen = ChosenCount;
    bool clearAll = (header.flags & ShadowClearAllFlag) != 0u;
    uint keepLight[ShadowMaxSlots]; // per slot: the chosen light that keeps it, or none
    [unroll] for (uint s = 0u; s < ShadowMaxSlots; s++)
        keepLight[s] = ShadowNone;

    // A light keeps the slot whose key is still its own.
    [loop] for (uint c = 0u; c < chosen; c++)
    {
        GpuLight light = Lights[Chosen[c]];
        float3 position = ShadowQuantize(light.positionRange.xyz);
        float3 direction = ShadowQuantize(normalize(light.directionOuterCos.xyz));
        [loop] for (uint s = 0u; s < slotCount; s++)
        {
            GpuLocalShadowSlot slot = ShadowSlots[s];
            bool matches = !clearAll && slot.state.x != 0u && keepLight[s] == ShadowNone
                && all(slot.key.xyz == position) && slot.key.w == light.positionRange.w
                && all(slot.key2.xyz == direction) && slot.key2.w == light.directionOuterCos.w;
            [flatten] if (matches)
            {
                keepLight[s] = Chosen[c];
                break;
            }
        }
    }

    // Every other slot is free: the lights without one take them, newest marked never drawn.
    [loop] for (uint c = 0u; c < chosen; c++)
    {
        uint lightIndex = Chosen[c];
        bool placed = false;
        [loop] for (uint s = 0u; s < slotCount && !placed; s++)
            placed = keepLight[s] == lightIndex;
        [branch] if (placed)
            continue;

        [loop] for (uint s = 0u; s < slotCount; s++)
        {
            [branch] if (keepLight[s] == ShadowNone)
            {
                GpuLight light = Lights[lightIndex];
                GpuLocalShadowSlot slot;
                slot.key = float4(ShadowQuantize(light.positionRange.xyz), light.positionRange.w);
                slot.key2 = float4(ShadowQuantize(normalize(light.directionOuterCos.xyz)), light.directionOuterCos.w);
                slot.state = uint4(1u, IsPointLight(light) ? ShadowFacesPerSlot : 1u, ShadowNone, lightIndex);
                ShadowSlots[s] = slot;
                keepLight[s] = lightIndex;
                break;
            }
        }
    }

    // Which slots draw this frame: the never-drawn first, then the oldest, within the face budget and the slices.
    uint refreshed = header.refreshedCount;
    uint budget = passParams.facesPerFrame;
    bool draws[ShadowMaxSlots];
    bool decided[ShadowMaxSlots];
    [unroll] for (uint s = 0u; s < ShadowMaxSlots; s++)
    {
        draws[s] = false;
        decided[s] = keepLight[s] == ShadowNone;
    }

    [loop] for (uint pick = 0u; pick < slotCount; pick++)
    {
        uint oldest = ShadowNone;
        uint oldestFrame = 0u;
        [loop] for (uint s = 0u; s < slotCount; s++)
        {
            [flatten] if (decided[s])
                continue;
            uint last = ShadowSlots[s].state.z;
            bool older = oldest == ShadowNone || last == ShadowNone || (oldestFrame != ShadowNone && last < oldestFrame);
            [flatten] if (older)
            {
                oldest = s;
                oldestFrame = last;
            }
        }

        [branch] if (oldest == ShadowNone)
            break;

        decided[oldest] = true;
        uint faces = ShadowSlots[oldest].state.y;
        bool isNew = ShadowSlots[oldest].state.z == ShadowNone;
        bool fits = refreshed + faces <= passParams.visibleSlots;
        bool draw = fits && (isNew || budget >= faces);
        [flatten] if (draw && !isNew)
            budget -= faces;
        draws[oldest] = draw;
        [flatten] if (draw)
            refreshed += faces;
    }

    // The rows, the lights' way to them, the refreshed list and the slots' state.
    uint listed = header.refreshedCount;
    bool anyLocal = false;
    [loop] for (uint s = 0u; s < slotCount; s++)
    {
        uint lightIndex = keepLight[s];
        GpuLocalShadowSlot slot = ShadowSlots[s];
        [branch] if (lightIndex == ShadowNone)
        {
            slot.state.x = 0u;
            ShadowSlots[s] = slot;
            [loop] for (uint face = 0u; face < ShadowFacesPerSlot; face++)
            {
                GpuShadowView row = ShadowViews[ShadowFaceRow(s, face)];
                row.flags = uint4(0u, 0u, 0u, 0u);
                ShadowViews[ShadowFaceRow(s, face)] = row;
            }

            continue;
        }

        anyLocal = true;
        GpuLight light = Lights[lightIndex];
        uint faces = slot.state.y;
        [loop] for (uint face = 0u; face < ShadowFacesPerSlot; face++)
        {
            uint rowIndex = ShadowFaceRow(s, face);
            [branch] if (face < faces)
            {
                uint slice = draws[s] ? listed : 0u;
                ShadowViews[rowIndex] = FaceRow(light, face, faces, s * ShadowFacesPerSlot + face, header, draws[s], slice);
                [flatten] if (draws[s])
                {
                    ShadowRefreshed[listed] = rowIndex;
                    listed++;
                }
            }
            else
            {
                GpuShadowView row = ShadowViews[rowIndex];
                row.flags = uint4(0u, 0u, 0u, 0u);
                ShadowViews[rowIndex] = row;
            }
        }

        light.shadow.x = ShadowFaceRow(s, 0u);
        light.shadow.y = faces;
        Lights[lightIndex] = light;

        slot.state.z = draws[s] ? FrameNumber : slot.state.z;
        slot.state.w = lightIndex;
        ShadowSlots[s] = slot;
    }

    header.refreshedCount = listed;
    header.flags |= anyLocal ? ShadowLocalFlag : 0u;
    header.slots.z = passParams.visibleSlots;
    ShadowHeader[0] = header;

    ShadowCullArgs[0] = counts.pageCount;
    ShadowCullArgs[1] = listed;
    ShadowCullArgs[2] = 1u;
    ShadowCullArgs[3] = 0u;
}
