// How a pixel asks the shadow atlas whether the sun, or a spot or point light, reaches it. Needs Include/Shade.hlsli
// (the cascade constants) and these declared by the including shader:
//   Texture2D<float> ShadowAtlas; SamplerComparisonState ShadowAtlasSampler; StructuredBuffer<GpuShadowRecord> ShadowRecords;
// The atlas is reverse-Z: what a map holds is the depth of the nearest caster, larger nearer the light, and the
// comparison sampler is GreaterOrEqual, so a tap is 1 (lit) where the receiver is at least as near the light as
// what was drawn. Every lookup pushes the receiver along its normal first, by a few texels of the map it reads
// (less the more it faces the light), which takes the acne off surfaces at a grazing angle without the
// peter-panning a plain depth offset gives, and a texel or so towards the light, for the surfaces that face it;
// and then clamps its uv half a texel inside its tile, so the filter's taps never read the neighbouring tile.

#ifndef MAGIC_SHADOWS_HLSLI
#define MAGIC_SHADOWS_HLSLI

#include "Include/Shade.hlsli"
#include "Include/Structs.hlsli"

// The filter: a fixed disc of taps, each a hardware 2x2 comparison, whose radius is FilterRadius metres in the
// texels of the map being read (clamped to MaxFilterTexels), so the penumbra is the same width in the world
// whichever cascade a pixel falls in.
#define SHADOW_TAPS 8
static const float2 ShadowTaps[SHADOW_TAPS] =
{
    float2(-0.7071, 0.7071), float2(-0.0000, -0.8750), float2(0.5303, 0.5303), float2(-0.6250, -0.0000),
    float2(0.3536, -0.3536), float2(-0.0000, 0.3750), float2(-0.1768, -0.1768), float2(0.1250, 0.0000)
};
static const float MaxFilterTexels = 4.0;
static const float MinFilterTexels = 0.5;

// The lit share of a disc of taps around uv, within the tile at tileOrigin of tileSize (in atlas uv).
float ShadowTaps8(float2 uv, float depth, float radiusTexels, float2 tileOrigin, float2 tileSize)
{
    float2 texel = ShadowAtlasUv.zw;
    float2 inset = tileOrigin + texel * 0.5;
    float2 outset = tileOrigin + tileSize - texel * 0.5;
    float2 stepUv = radiusTexels * texel;
    float lit = 0.0;
    [unroll] for (uint tap = 0u; tap < SHADOW_TAPS; tap++)
    {
        float2 at = clamp(uv + ShadowTaps[tap] * stepUv, inset, outset);
        lit += ShadowAtlas.SampleCmpLevelZero(ShadowAtlasSampler, at, depth);
    }

    return lit / (float)SHADOW_TAPS;
}

// One cascade's answer for a camera-relative position: 1 lit, 0 shadowed, between in the penumbra.
float CascadeVisibility(uint cascade, float3 positionRel, float3 normal, float facing)
{
    float4 row = Cascade[cascade];
    float3 offset = normal * (row.y * ShadowParams.w * (1.0 - facing)) - SunDirection * (row.y * ShadowDepthBias);
    float4 clip = mul(CascadeViewProj[cascade], float4(positionRel + offset, 1.0));
    float2 local = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    float2 uv = row.zw + local * ShadowAtlasUv.xy;
    float radiusTexels = clamp(ShadowParams.z / max(row.y, 1e-6), MinFilterTexels, MaxFilterTexels);
    return ShadowTaps8(uv, clip.z, radiusTexels, row.zw, ShadowAtlasUv.xy);
}

// The sun's visibility at a pixel: the first cascade whose range reaches the pixel's view depth, blended into the
// next over the last BlendFraction of its range, and into full light past the last one. The selection is uniform
// per lane and samples only inside the one branch, after the guard.
float SunShadow(float3 positionRel, float3 normal, float viewDepth, float facing, out uint cascadeOut)
{
    uint count = (uint)ShadowParams.x;
    uint cascade = count;
    [unroll] for (uint i = 0u; i < SHADOW_MAX_CASCADES; i++)
        cascade = (i < count && cascade == count && viewDepth <= Cascade[i].x) ? i : cascade;

    float visible = 1.0;
    [branch] if (cascade < count)
    {
        float far = Cascade[cascade].x;
        float blendStart = far * (1.0 - ShadowParams.y);
        float blend = saturate((viewDepth - blendStart) / max(far - blendStart, 1e-4));
        visible = CascadeVisibility(cascade, positionRel, normal, facing);
        [branch] if (blend > 0.0)
        {
            float next = cascade + 1u < count ? CascadeVisibility(cascade + 1u, positionRel, normal, facing) : 1.0;
            visible = lerp(visible, next, blend);
        }
    }

    cascadeOut = cascade;
    return visible;
}

// The sun's visibility at a camera-relative point that has no view depth (a voxel of the GI): the finest cascade
// whose map holds the point, full light past the last.
float SunVisibilityAt(float3 positionRel, float3 normal)
{
    uint count = (uint)ShadowParams.x;
    uint cascade = count;
    [loop] for (uint i = 0u; i < count && cascade == count; i++)
    {
        float4 clip = mul(CascadeViewProj[i], float4(positionRel, 1.0));
        if (all(abs(clip.xy) <= 0.98) && clip.z >= 0.0 && clip.z <= 1.0)
            cascade = i;
    }

    float visible = 1.0;
    [branch] if (cascade < count)
        visible = CascadeVisibility(cascade, positionRel, normal, saturate(dot(normal, -SunDirection)));
    return visible;
}

// The face of a six-faced record that a direction from the light falls in: +X, -X, +Y, -Y, +Z, -Z.
uint ShadowFaceOf(float3 fromLight)
{
    float3 magnitude = abs(fromLight);
    uint face = 4u + (fromLight.z < 0.0 ? 1u : 0u);
    [flatten] if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z)
        face = fromLight.x < 0.0 ? 1u : 0u;
    else if (magnitude.y >= magnitude.z)
        face = fromLight.y < 0.0 ? 3u : 2u;
    return face;
}

// A spot or point light's visibility at a camera-relative position, through the light's record(s): the one face a spot
// has, or the face of six the pixel falls in. The light's reach was tested by the caller: this only reads the map.
float LocalShadow(GpuLight light, float3 positionRel, float3 normal)
{
    float3 fromLight = positionRel - (light.positionRange.xyz - CameraPos);
    uint face = light.shadow.y > 1u ? ShadowFaceOf(fromLight) : 0u;
    GpuShadowRecord record = ShadowRecords[light.shadow.x + face];

    // Metres per texel at this distance from the light: the map's texel grows linearly with it.
    float distance = length(fromLight);
    float texelWorld = record.params.x * distance;
    float3 toLight = -fromLight / max(distance, 1e-6);
    float facing = saturate(dot(normal, toLight));
    float3 offset = normal * (texelWorld * ShadowParams.w * (1.0 - facing)) + toLight * (texelWorld * ShadowDepthBias);

    float4 clip = mul(record.viewProj, float4(positionRel + offset, 1.0));
    float3 ndc = clip.xyz / max(clip.w, 1e-6);
    float2 local = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    float2 uv = record.rectUv.xy + local * record.rectUv.zw;
    float radiusTexels = clamp(ShadowParams.z / max(texelWorld, 1e-6), MinFilterTexels, MaxFilterTexels);
    return ShadowTaps8(uv, ndc.z, radiusTexels, record.rectUv.xy, record.rectUv.zw);
}

#endif
