// How a pixel asks the shadow atlas whether the sun, or a spot or point light, reaches it. The caller passes the
// filter and bias settings (ShadowFilter) and declares these before including it:
//   Texture2D<float> ShadowAtlas; SamplerComparisonState ShadowAtlasSampler;
//   StructuredBuffer<GpuShadowView> ShadowViews; StructuredBuffer<GpuShadowHeader> ShadowHeader;
// The views were planned on the GPU (Include/ShadowViews.hlsli): a cascade is a row, a local light's faces are
// rows, each with its tile and matrices relative to its own eye. The atlas is reverse-Z: what a map holds is
// the depth of the nearest caster, larger nearer the light, and the comparison sampler is GreaterOrEqual, so a
// tap is 1 (lit) where the receiver is at least as near the light as what was drawn. Every lookup pushes the
// receiver along its normal first, by a few texels of the map it reads (less the more it faces the light), which
// takes the acne off surfaces at a grazing angle without the peter-panning a plain depth offset gives, and a
// texel or so towards the light, for the surfaces that face it; and then clamps its uv half a texel inside its
// tile, so the filter's taps never read the neighbouring tile.

#ifndef MAGIC_SHADOWS_HLSLI
#define MAGIC_SHADOWS_HLSLI

#include "Include/ShadowViews.hlsli"

// How the maps are read: the share of a cascade's range it blends into the next over, the penumbra's radius in
// metres, and the receiver's offsets in texels along its normal and towards the light.
struct ShadowFilter
{
    float blend;
    float radius;
    float normalBias;
    float depthBias;
};

// The filter: a fixed disc of taps, each a hardware 2x2 comparison, whose radius is FilterRadius metres in the
// texels of the map being read (clamped to MaxFilterTexels), so the penumbra is the same width in the world
// whichever cascade a pixel falls in. A shader defining SHADOW_ONE_TAP 1 before the include reads one comparison
// instead: the GI's voxels are far wider than any penumbra.
#define SHADOW_TAPS 8
static const float2 ShadowTaps[SHADOW_TAPS] =
{
    float2(-0.7071, 0.7071), float2(-0.0000, -0.8750), float2(0.5303, 0.5303), float2(-0.6250, -0.0000),
    float2(0.3536, -0.3536), float2(-0.0000, 0.3750), float2(-0.1768, -0.1768), float2(0.1250, 0.0000)
};
static const float MaxFilterTexels = 4.0;
static const float MinFilterTexels = 0.5;

#ifndef SHADOW_ONE_TAP
#define SHADOW_ONE_TAP 0
#endif

// The lit share of a disc of taps around uv, within the tile at tileOrigin of tileSize (in atlas uv).
float FilteredShadow(float2 uv, float depth, float radiusTexels, float2 tileOrigin, float2 tileSize)
{
    float2 texel = ShadowHeader[0].atlasTexel.xy;
    float2 inset = tileOrigin + texel * 0.5;
    float2 outset = tileOrigin + tileSize - texel * 0.5;
#if SHADOW_ONE_TAP
    return ShadowAtlas.SampleCmpLevelZero(ShadowAtlasSampler, clamp(uv, inset, outset), depth);
#else
    float2 stepUv = radiusTexels * texel;
    float lit = 0.0;
    [unroll] for (uint tap = 0u; tap < SHADOW_TAPS; tap++)
    {
        float2 at = clamp(uv + ShadowTaps[tap] * stepUv, inset, outset);
        lit += ShadowAtlas.SampleCmpLevelZero(ShadowAtlasSampler, at, depth);
    }

    return lit / (float)SHADOW_TAPS;
#endif
}

// One cascade's answer for a camera-relative position: 1 lit, 0 shadowed, between in the penumbra.
float CascadeVisibility(ShadowFilter filter, uint cascade, float3 positionRel, float3 normal, float facing)
{
    GpuShadowView view = ShadowViews[cascade];
    float texelWorld = view.eye.w;
    float3 offset = normal * (texelWorld * filter.normalBias * (1.0 - facing)) - SunDirection * (texelWorld * filter.depthBias);
    float3 positionEye = positionRel + (CameraPos - view.eye.xyz) + offset;
    float4 clip = mul(view.viewProj, float4(positionEye, 1.0));
    float2 local = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    float2 uv = view.tileUv.xy + local * view.tileUv.zw;
    float radiusTexels = clamp(filter.radius / max(texelWorld, 1e-6), MinFilterTexels, MaxFilterTexels);
    return FilteredShadow(uv, clip.z, radiusTexels, view.tileUv.xy, view.tileUv.zw);
}

// The sun's far view at a camera-relative point (row ShadowFarRow): full light where it is off or does not hold the
// point, fading to that over the last tenth of its square so its edge is never a line.
float FarVisibility(ShadowFilter filter, float3 positionRel, float3 normal, float facing)
{
    GpuShadowView view = ShadowViews[ShadowFarRow];
    if ((view.flags.x & ShadowViewUsed) == 0u)
        return 1.0;

    float4 clip = mul(view.viewProj, float4(positionRel + (CameraPos - view.eye.xyz), 1.0));
    float inside = saturate((1.0 - max(abs(clip.x), abs(clip.y))) * 10.0);
    [branch] if (inside <= 0.0)
        return 1.0;

    return lerp(1.0, CascadeVisibility(filter, ShadowFarRow, positionRel, normal, facing), inside);
}

// The sun's visibility at a pixel: the first cascade whose range reaches the pixel's view depth, blended into the
// next over the last BlendFraction of its range, and into the far view past the last one. The selection is uniform
// per lane and samples only inside the one branch, after the guard. cascadeOut is ShadowFarRow past the cascades
// where the far view is, ShadowFarRow + 1 where nothing shadows the point.
float SunShadow(ShadowFilter filter, float3 positionRel, float3 normal, float viewDepth, float facing, out uint cascadeOut)
{
    uint count = ShadowHeader[0].cascadeCount;
    uint cascade = count;
    [unroll] for (uint i = 0u; i < ShadowMaxCascades; i++)
        cascade = (i < count && cascade == count && viewDepth <= ShadowViews[i].range.x) ? i : cascade;

    float visible = 1.0;
    [branch] if (cascade < count)
    {
        float far = ShadowViews[cascade].range.x;
        float blendStart = far * (1.0 - filter.blend);
        float blend = saturate((viewDepth - blendStart) / max(far - blendStart, 1e-4));
        visible = CascadeVisibility(filter, cascade, positionRel, normal, facing);
        [branch] if (blend > 0.0)
        {
            float next = cascade + 1u < count ? CascadeVisibility(filter, cascade + 1u, positionRel, normal, facing) : FarVisibility(filter, positionRel, normal, facing);
            visible = lerp(visible, next, blend);
        }
    }
    else
    {
        visible = FarVisibility(filter, positionRel, normal, facing);
    }

    bool farUsed = (ShadowViews[ShadowFarRow].flags.x & ShadowViewUsed) != 0u;
    cascadeOut = cascade < count ? cascade : farUsed ? ShadowFarRow : ShadowFarRow + 1u;
    return visible;
}

// The sun's visibility at a camera-relative point that has no view depth (a voxel of the GI): the finest cascade
// whose map holds the point, the far view past the last.
float SunVisibilityAt(ShadowFilter filter, float3 positionRel, float3 normal)
{
    uint count = ShadowHeader[0].cascadeCount;
    uint cascade = count;
    [loop] for (uint i = 0u; i < count && cascade == count; i++)
    {
        GpuShadowView view = ShadowViews[i];
        float4 clip = mul(view.viewProj, float4(positionRel + (CameraPos - view.eye.xyz), 1.0));
        if (all(abs(clip.xy) <= 0.98) && clip.z >= 0.0 && clip.z <= 1.0)
            cascade = i;
    }

    float facing = saturate(dot(normal, -SunDirection));
    return cascade < count ? CascadeVisibility(filter, cascade, positionRel, normal, facing) : FarVisibility(filter, positionRel, normal, facing);
}

// A spot or point light's visibility at a camera-relative position, through the light's shadow views: the one face
// a spot has, or the face of six the pixel falls in. The light's reach was tested by the caller: this only reads the map.
// A light's faces are drawn together, from where it was then (their eye), which a moving light whose turn has not
// come again has since left: the pixel is placed against that eye, not the light's position now, or a face would
// compare depths measured from two places (the side faces of a light moving sideways all read as shadowed, leaving
// the square its downward face covers lit).
float LocalShadow(ShadowFilter filter, GpuLight light, float3 positionRel, float3 normal)
{
    float3 fromLight = positionRel - (ShadowViews[light.shadow.x].eye.xyz - CameraPos);
    uint face = light.shadow.y > 1u ? ShadowFaceOf(fromLight) : 0u;
    GpuShadowView view = ShadowViews[light.shadow.x + face];

    // Metres per texel at this distance from the light: the map's texel grows linearly with it.
    float distance = length(fromLight);
    float texelWorld = view.eye.w * distance;
    float3 toLight = -fromLight / max(distance, 1e-6);
    float facing = saturate(dot(normal, toLight));
    float3 offset = normal * (texelWorld * filter.normalBias * (1.0 - facing)) + toLight * (texelWorld * filter.depthBias);

    float4 clip = mul(view.viewProj, float4(fromLight + offset, 1.0));
    float3 ndc = clip.xyz / max(clip.w, 1e-6);
    float2 local = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    float2 uv = view.tileUv.xy + local * view.tileUv.zw;
    float radiusTexels = clamp(filter.radius / max(texelWorld, 1e-6), MinFilterTexels, MaxFilterTexels);
    return FilteredShadow(uv, ndc.z, radiusTexels, view.tileUv.xy, view.tileUv.zw);
}

#endif
