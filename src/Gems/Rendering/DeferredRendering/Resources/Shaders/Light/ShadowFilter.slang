// Contact-hardening shadows from a shadow map (Fernando, "Percentage-Closer Soft Shadows", 2005), in the form of
// Bavoil, "Integrating Realistic Soft Shadows into Your Game Engine", NVIDIA, 2008. A blocker search finds how far the
// casters around a point are in front of it, which gives the width of the penumbra there, and a percentage-closer
// filter of that width gives the share of the light that reaches the point: sharp where the caster meets the shadow
// and softer further from it. A wide search reads a min/max mip of the map, as the GDC 2008 slides of the same method
// propose; the PCF reads the map itself. The taps lie on a Vogel disc rotated for each pixel (Vogel, "A better way to
// construct the sunflower head", 1979). Every depth is reversed: larger is nearer the light.
module "Light/ShadowFilter";

import Core.Heap;
import Core.Math;

// What a filter reads: the depth atlas, a sampler with clamped edges for its gathers, and the min and max mips of the
// atlas, level 0 at half its size, 0 where none were made.
public struct ShadowMaps
{
    public uint atlas;
    public uint sampler;
    public uint minMips;
    public uint maxMips;
    public uint mipLevels;
}

// The most texels a blocker search reads across in one level: a wider search reads a mip.
static const float WidestSearch = 16.0;

// The golden angle, in radians: the turn between neighbouring taps of a Vogel disc.
static const float GoldenAngle = 2.39996323;

// Tap index of count on the unit disc, turned by rotation radians.
public float2 GetVogelTap(uint index, uint count, float rotation)
{
    float radius = sqrt((float(index) + 0.5) / float(count));
    float angle = float(index) * GoldenAngle + rotation;
    return radius * float2(cos(angle), sin(angle));
}

// A position in texels of the atlas kept inside the rectangle of its view, by as many texels as a read around it
// reaches, so no read takes a texel of another view.
float2 KeepInRect(float2 position, uint4 rect, float margin)
{
    float2 low = float2(rect.xy) + margin;
    float2 high = float2(rect.xy + rect.zw) - margin;
    return clamp(position, low, high);
}

// What a blocker search found: the mean reversed depth of the casters nearer the light than the receiver, and how many
// taps found one, 0 where none did.
public struct Blockers
{
    public float depth;
    public float count;
}

// The casters within radius texels of position that are nearer the light than receiver.
public Blockers FindBlockers(ShadowMaps maps, float2 position, float receiver, float radius, uint4 rect, float rotation,
    uint taps)
{
    float sum = 0.0;
    float found = 0.0;
    // The mip level whose texels make the search at most WidestSearch of them across, level 0 at half the size.
    int level = int(ceil(log2(max(2.0 * radius / WidestSearch, 1.0)))) - 1;
    if (level >= 0 && maps.minMips != 0u && maps.maxMips != 0u)
    {
        uint mip = uint(min(level, int(maps.mipLevels) - 1));
        float scale = float(2u << mip);
        TextureRef minDepths = { maps.minMips };
        TextureRef maxDepths = { maps.maxMips };
        for (uint i = 0u; i < taps; i++)
        {
            float2 tap = KeepInRect(position + GetVogelTap(i, taps, rotation) * radius, rect, scale * 0.5);
            uint2 texel = uint2(tap / scale);
            float nearest = maxDepths.Load(texel, mip).x;
            if (nearest <= receiver)
                continue;

            // Some of the texels under it are nearer the light: their mean lies between the nearest and the farthest
            // of them that is still nearer than the receiver.
            float farthest = max(minDepths.Load(texel, mip).x, receiver);
            sum += 0.5 * (nearest + farthest);
            found += 1.0;
        }
    }
    else
    {
        TextureRef depths = { maps.atlas };
        SamplerRef sampler = { maps.sampler };
        float2 size = float2(depths.GetSize(0u));
        for (uint i = 0u; i < taps; i++)
        {
            float2 tap = KeepInRect(position + GetVogelTap(i, taps, rotation) * radius, rect, 1.0);
            float4 gathered = depths.GatherRed(sampler, round(tap) / size);
            float4 isBlocker = select(gathered > receiver, float4(1.0), float4(0.0));
            sum += dot(gathered, isBlocker);
            found += dot(isBlocker, float4(1.0));
        }
    }

    Blockers blockers = { found > 0.0 ? sum / found : 0.0, found };
    return blockers;
}

// The share of the light that reaches a point whose reversed depth is receiver at position, in texels of the atlas,
// filtered over radius texels: each tap compares the four texels around it and weighs them as a bilinear sample would.
public float FilterShadow(ShadowMaps maps, float2 position, float receiver, float radius, uint4 rect, float rotation,
    uint taps)
{
    TextureRef depths = { maps.atlas };
    SamplerRef sampler = { maps.sampler };
    float2 size = float2(depths.GetSize(0u));
    float lit = 0.0;
    for (uint i = 0u; i < taps; i++)
    {
        float2 tap = KeepInRect(position + GetVogelTap(i, taps, rotation) * radius, rect, 1.0);
        // The texel centres around the tap, and where the tap falls between them.
        float2 corner = floor(tap - 0.5);
        float2 f = tap - 0.5 - corner;
        float4 gathered = depths.GatherRed(sampler, (corner + 1.0) / size);
        float4 isLit = select(receiver >= gathered, float4(1.0), float4(0.0));
        // x lower left, y lower right, z upper right, w upper left, with "lower" one texel further down.
        lit += lerp(lerp(isLit.w, isLit.z, f.x), lerp(isLit.x, isLit.y, f.x), f.y);
    }

    return lit / float(taps);
}
