// Marks the pages of shadow the view's pixels read, a thread a pixel: the sun's level as fine as the pixel, the first
// whose window holds it, and the next three coarser, which its soft shadow is searched and filtered through; and, for
// each point or spot light that casts a shadow and reaches the pixel (its z-bin and tile, as the lighting finds them),
// its face's page at the mip as fine as the pixel, and at the coarsest, which it is read through while its own waits,
// put into this frame's table. A page marked is wanted this frame; the allocator gives it a page of the pool when it
// has none. Most of a page's pixels find it marked already, and leave it be.

#include "Include/LightCulling.hlsli"
#include "Include/VirtualShadows.hlsli"
#include "Lighting/Lights.hlsli"

// The coarser sun levels marked past the pixel's own, which its penumbra may be filtered through.
static const uint SunSoftLevels = 3u;

// Notes an entry wanted this frame, unless it is already.
void Want(uint row, uint frame)
{
    if (ShadowTables.Load(row * 16u + 8u) != frame)
    {
        ShadowTables.Store(row * 16u + 8u, frame);
    }
}

void MarkSun(GpuSunClipmap sun, float3 position, float viewDepth)
{
    uint level = SunLevelFor(sun, viewDepth);
    uint marked = 0u;
    [loop] for (; level < sun.levels && marked <= SunSoftLevels; level++)
    {
        int2 page;
        float2 place;
        if (SunPageOf(sun, SunLevelOf(level), position, page, place))
        {
            Want(sun.sunTable + SunEntryOf(level, page), sun.frame);
            marked++;
        }
    }
}

void MarkLightPage(GpuSunClipmap sun, uint slot, uint face, float2 onFace, uint mip)
{
    uint side = FacePages(mip);
    uint2 page = min((uint2)(onFace * side), side - 1u);
    uint entry = InsertLightEntry(ShadowTables, sun.lightTable, LightKeyOf(slot, face, mip, page), sun.lightEntries);
    if (entry != NoView)
    {
        Want(sun.lightTable + entry, sun.frame);
    }
}

void MarkLight(GpuSunClipmap sun, uint slot, float3 toLight, float viewDepth)
{
    uint mip = LightMipFor(viewDepth / LodScale, length(toLight));
    uint face = FaceOf(-toLight);
    float ahead;
    float2 onFace = FacePlaceOf(face, -toLight, ahead);
    MarkLightPage(sun, slot, face, onFace, mip);
    if (mip < LightMips - 1u)
    {
        MarkLightPage(sun, slot, face, onFace, LightMips - 1u);
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

    // Never, as no pixel is that far, but it keeps every read bound with its sampler, though read texel by texel.
    depth += id.x > 0x7FFFFFFFu ? KeepReadsAlive(float2(0.0, 0.0)) : 0.0;
    if (depth <= 0.0)
    {
        return;
    }

    GpuSunClipmap sun = SunClipmapOf();
    float2 pixel = (float2)id.xy + 0.5;
    float viewDepth = ViewDepth(depth);
    float3 position = ViewToWorld(ViewPosition(pixel, viewDepth));
    if (any(SunIlluminance > 0.0))
    {
        MarkSun(sun, position, viewDepth);
    }

    // The lights reaching the pixel, as the lighting walks them (Lighting/Shade.hlsli's LocalLights).
    uint bin = ZBinOf(viewDepth);
    uint first = LightBins.Load(bin * 8u);
    uint last = LightBins.Load(bin * 8u + 4u);
    uint2 tile = id.xy / LightTileSize;
    uint tileWords = (tile.y * TileCountX + tile.x) * TileWords;
    uint firstWord = WaveActiveMin(first <= last ? first >> 5u : 0xFFFFFFFFu);
    uint lastWord = WaveActiveMax(first <= last ? last >> 5u : 0u);
    [loop] for (uint word = firstWord; word <= lastWord && word < TileWords; word++)
    {
        uint mask = WaveActiveBitOr(LightTiles.Load((tileWords + word) * 4u) & RangeMask(word, first, last));
        [loop] while (mask != 0u)
        {
            uint bit = firstbitlow(mask);
            mask &= mask - 1u;
            GpuLight light = LOAD(Lights, GpuLight, word * 32u + bit);
            float3 toLight = light.position - position;
            float distanceSq = dot(toLight, toLight);
            float spot = SpotAttenuation(toLight * rsqrt(max(distanceSq, 1e-8)), light.forward, float2(light.spotScale, light.spotOffset));
            [branch] if (light.shadow != NoShadowFace && distanceSq * light.invRangeSquared < 1.0 && spot > 0.0)
            {
                MarkLight(sun, light.shadow, toLight, viewDepth);
            }
        }
    }
}
