// The pool every shadow is drawn into, fitted to the view, a cell a page: a sun page in its level's colour, finest first
// round the wheel, a light's page white, a free page black; dimmed where the page waits to be drawn, and fading the
// longer since a pixel last read it. The last quarter holds the small pages, sixteen to a cell. Teal where the world
// draws no shadows.

#include "Debug/View.hlsli"
#if defined(HAS_ShadowTables) && defined(HAS_ShadowPool) && defined(HAS_SunClipmap)
#include "Include/VirtualShadows.hlsli"
#endif

float4 main(PassVaryings input) : SV_Target0
{
#if defined(HAS_ShadowTables) && defined(HAS_ShadowPool) && defined(HAS_SunClipmap)
    GpuSunClipmap sun = SunClipmapOf();
    uint2 texel = (uint2)(input.uv * PoolSize());
    uint2 cell = texel / PageTexels;
    uint whole = cell.y * sun.poolAcross + cell.x;
    bool isSmall = whole >= sun.wholePages;
    uint2 part = (texel % PageTexels) / SmallTexels;
    uint record = isSmall ? sun.poolAcross * sun.poolAcross + (whole - sun.wholePages) * 16u + part.y * 4u + part.x : whole;
    uint2 held = ShadowTables.Load2(record * 16u);
    uint owner = held.x;
    bool isLine = any(texel % (isSmall ? SmallTexels : PageTexels) < 2u);
    if (owner == 0u)
    {
        return float4(isLine ? float3(0.08, 0.08, 0.08) : float3(0.0, 0.0, 0.0), 1.0);
    }

    bool isLight = (owner & LightOwner) != 0u;
    uint row = isLight ? sun.lightTable + (owner & ~LightOwner) : sun.sunTable + owner - 1u;
    bool isDirty = (ShadowTables.Load(row * 16u) & PageDirty) != 0u;
    float3 color = isLight ? float3(0.9, 0.9, 0.9) : HueColor((float)(((owner - 1u) / SunEntriesPerLevel) % 8u) / 8.0);
    float age = (float)min(sun.frame - held.y, 60u) / 60.0;
    color *= (isDirty ? 0.35 : 1.0) * (1.0 - 0.5 * age) * (isLine ? 0.5 : 1.0);
    return float4(color, 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
