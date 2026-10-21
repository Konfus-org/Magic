// Marks the pages of shadow the view's pixels read, a thread a pixel: every clipmap level the lighting's lookup can
// read for the pixel (Include/VirtualShadows.hlsli's KeyLightVirtualShadow), from ClipmapFinerLevels finer than the
// finest its dither can pick to ClipmapSoftLevels coarser than the coarsest, each at the point the lookup reads it,
// pushed out along the pixel's normal by that level's texel; and, for each point or spot light that casts a shadow and
// reaches the pixel (its z-bin and tile, as the lighting finds them), its face's page at the mip as fine as the pixel,
// and at the coarsest, which it is read through while its own waits, put into this frame's table. A page marked is
// wanted this frame; the allocator gives it a page of the pool when it has none. Most of a page's pixels find it marked
// already, and leave it be.

#include "Include/LightCulling.hlsli"
#include "Include/Packing.hlsli"
#include "Include/VirtualShadows.hlsli"
#include "Lighting/Lights.hlsli"

// Notes an entry wanted this frame, unless it is already.
void Want(uint row, uint frame)
{
    if (ShadowTables.Load(row * 16u + 8u) != frame)
    {
        ShadowTables.Store(row * 16u + 8u, frame);
    }
}

void MarkKeyLight(GpuClipmap clipmap, float3 position, float3 normal, float viewDepth)
{
    uint finest = ClipmapLevelFor(clipmap, viewDepth, 0.0);
    uint level = finest > ClipmapFinerLevels ? finest - ClipmapFinerLevels : 0u;
    uint last = min(ClipmapLevelFor(clipmap, viewDepth, 1.0) + ClipmapSoftLevels, clipmap.levels - 1u);
    [loop] for (; level <= last; level++)
    {
        int2 page;
        float2 place;
        if (ClipmapPageOf(clipmap, ClipmapLevelOf(level), ClipmapMoved(clipmap, level, position, normal), page, place))
        {
            Want(clipmap.levelTable + ClipmapEntryOf(level, page), clipmap.frame);
        }
    }
}

void MarkLightPage(GpuClipmap clipmap, uint slot, uint face, float2 onFace, uint mip)
{
    uint side = FacePages(mip);
    uint2 page = min((uint2)(onFace * side), side - 1u);
    uint entry = InsertLightEntry(ShadowTables, clipmap.lightTable, LightKeyOf(slot, face, mip, page), clipmap.lightEntries);
    if (entry != NoView)
    {
        Want(clipmap.lightTable + entry, clipmap.frame);
    }
}

void MarkLight(GpuClipmap clipmap, uint slot, float3 toLight, float viewDepth)
{
    uint mip = LightMipFor(viewDepth / PixelScale, length(toLight));
    uint face = FaceOf(-toLight);
    float ahead;
    float2 onFace = FacePlaceOf(face, -toLight, ahead);
    MarkLightPage(clipmap, slot, face, onFace, mip);
    if (mip < LightMips - 1u)
    {
        MarkLightPage(clipmap, slot, face, onFace, LightMips - 1u);
    }
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)ViewSize))
    {
        return;
    }

    float depth = Depth.Load(int3(id.xy, 0));
    if (depth <= 0.0)
    {
        return;
    }

    GpuClipmap clipmap = ClipmapOf();
    float2 pixel = (float2)id.xy + 0.5;
    float viewDepth = ViewDepth(depth);
    float3 position = ViewToWorld(ViewPosition(pixel, viewDepth));
    if (any(KeyLightIlluminance > 0.0))
    {
        float3 normal = OctDecode(Normal.Load(int3(id.xy, 0)).xy * 2.0 - 1.0);
        MarkKeyLight(clipmap, position, normal, viewDepth);
    }

    // The lights reaching the pixel, as the lighting walks them (Lighting/Shade.hlsli's LocalLights).
    LightWalk walk = LightWalkOf(pixel, viewDepth);
    [loop] for (uint word = walk.firstWord; word <= walk.lastWord && word < TileWords; word++)
    {
        uint mask = LightWalkMask(walk, word);
        [loop] while (mask != 0u)
        {
            uint bit = firstbitlow(mask);
            mask &= mask - 1u;
            GpuLight light = LOAD(Lights, GpuLight, WaveReadLaneFirst(word * 32u + bit));
            float3 toLight = light.position - position;
            float distanceSq = dot(toLight, toLight);
            float spot = SpotAttenuation(toLight * rsqrt(max(distanceSq, 1e-8)), light.forward, float2(light.spotScale, light.spotOffset));
            [branch] if (light.shadow != NoShadowFace && distanceSq * light.invRangeSquared < 1.0 && spot > 0.0)
            {
                MarkLight(clipmap, light.shadow, toLight, viewDepth);
            }
        }
    }
}
