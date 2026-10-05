// What the ambient occlusion passes share: how the raw result is packed between them, the spatial noise the
// horizon search is jittered with, and the search radius in pixels. Needs the frame block through
// Include/GBuffer.hlsli.
//
// The raw texel (Gtao.comp.hlsl writes it, AoBlur.comp.hlsl reads it) is rgba16f: x the visibility, y the
// view depth it was computed at (the blur weighs its taps by depth), zw the bent normal in view space,
// octahedrally encoded. The final texel (AoBlur writes it, the lighting reads it) is rgba8: xyz the bent
// normal in world space packed into 0..1, w the visibility.

#ifndef MAGIC_AO_COMMON_HLSLI
#define MAGIC_AO_COMMON_HLSLI

#include "Include/GBuffer.hlsli"

// The pattern repeats every 4 x 4 texels, so a 4 x 4 blur sees every rotation and offset once: a fixed
// spatial dither, nothing temporal, so the result never swims and needs no history.
static const uint AoNoiseSize = 4u;

void AoNoise(uint2 pixel, out float rotation, out float offset)
{
    uint x = pixel.x & 3u, y = pixel.y & 3u;
    rotation = (float)(((x + y) & 3u) * 4u + x) / 16.0;
    offset = (float)((y - x) & 3u) * 0.25;
}

// The search radius in pixels of the occlusion texture (scale of the view's): the world radius projected at
// this depth, no less than a texel and no more than maxPixels (a near surface would otherwise sample the whole
// screen).
float AoPixelRadius(float viewDepth, float radiusMetres, float maxPixels, float scale)
{
    float spread = IsOrthographic != 0u ? 1.0 : max(viewDepth, Near);
    float pixels = radiusMetres * ProjScale.y * ViewSize.y * 0.5 / spread;
    return clamp(pixels, 1.0, maxPixels) * scale;
}

// A unit vector to two numbers in -1..1 and back (octahedral mapping): the bent normal's two spare channels.
float2 OctahedralEncode(float3 direction)
{
    float3 scaled = direction / (abs(direction.x) + abs(direction.y) + abs(direction.z));
    float2 flat = scaled.xy;
    float2 folded = (1.0 - abs(scaled.yx)) * float2(scaled.x >= 0.0 ? 1.0 : -1.0, scaled.y >= 0.0 ? 1.0 : -1.0);
    return scaled.z >= 0.0 ? flat : folded;
}

float3 OctahedralDecode(float2 encoded)
{
    float3 direction = float3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    float fold = saturate(-direction.z);
    direction.xy += float2(direction.x >= 0.0 ? -fold : fold, direction.y >= 0.0 ? -fold : fold);
    return NormalizeOrZero(direction);
}

float4 EncodeAoRaw(float visibility, float viewDepth, float3 bentView)
{
    return float4(visibility, viewDepth, OctahedralEncode(bentView));
}

float4 EncodeAo(float3 bentWorld, float visibility)
{
    return float4(bentWorld * 0.5 + 0.5, visibility);
}

// A view-space normal from the gbuffer's world-space one.
float3 ViewNormal(float3 worldNormal)
{
    return mul(View, float4(worldNormal, 0.0)).xyz;
}

#endif
