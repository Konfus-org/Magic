// The sky's shadow on the world: the share of the key light's light, by colour, that reaches the ground around the
// camera through the sky's air and what it has in front of the light (Include/Sky.hlsli's LightThrough), which a sky
// draw of its own lays into the frame's SkyShadow texture (Templates/SkyShadow.frag.hlsl) and whatever the key light
// lights reads back by SkyShadowAt. Only a sun or a moon's light comes through the sky (KeyLightThroughSky): a light
// alone reaches everything whole.
//
// The texture covers a square of the level plane through the camera, its middle moved in whole steps of the world, so
// what it holds of clouds that stand still stays where it is as the camera moves. A place is looked up where its way
// toward the light crosses that plane: the share is the same all along that way, beneath what casts it, so one plane
// serves every height. Past the square, it is the square's edge's.

#ifndef MAGIC_SKY_SHADOW_HLSLI
#define MAGIC_SKY_SHADOW_HLSLI

#include "Include/Frame.hlsli"

// Metres a side of the square the shadow covers, and the steps its middle moves by: a whole number of texels for any
// SkyShadow a multiple of 64 texels a side.
static const float SkyShadowExtent = 8000.0;
static const float SkyShadowStep = SkyShadowExtent / 64.0;

// The lowest the light's height above the horizon is taken as, as its way to a place nears level with the plane.
static const float SkyShadowLowestLight = 0.01;

// Where in the world, x and z, the middle of the square is this frame.
float2 SkyShadowMiddle()
{
    return round(CameraPosition.xz / SkyShadowStep) * SkyShadowStep;
}

// Where a place of the world, x and z, falls on the square, as a texture's uv.
float2 SkyShadowUv(float2 place)
{
    return (place - SkyShadowMiddle()) / SkyShadowExtent + 0.5;
}

// The place of the world, x and z, at a uv of the square.
float2 SkyShadowPlace(float2 uv)
{
    return SkyShadowMiddle() + (uv - 0.5) * SkyShadowExtent;
}

#if defined(HAS_SkyShadow)
// The shadow at a uv of the square through a cubic B-spline over its texels, in four bilinear taps (Sigg & Hadwiger
// 2005, "Fast Third-Order Texture Filtering"): smooth across texels where a bilinear tap shows their grid, as a cloud's
// shadow crosses a surface close by and a texel covers much of the view.
float3 SkyShadowSmooth(float2 uv)
{
    float2 size;
    SkyShadow.GetDimensions(size.x, size.y);
    float2 texel = uv * size - 0.5;
    float2 cell = floor(texel);
    float2 f = texel - cell;
    float2 f2 = f * f;
    float2 f3 = f2 * f;
    float2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
    float2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
    float2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
    float2 w3 = f3 / 6.0;
    float2 low = w0 + w1;
    float2 high = w2 + w3;
    float2 lowAt = (cell - 0.5 + w1 / low) / size;
    float2 highAt = (cell + 1.5 + w3 / high) / size;
    float3 a = SkyShadow.SampleLevel(SkyShadowSampler, lowAt, 0.0).rgb;
    float3 b = SkyShadow.SampleLevel(SkyShadowSampler, float2(highAt.x, lowAt.y), 0.0).rgb;
    float3 c = SkyShadow.SampleLevel(SkyShadowSampler, float2(lowAt.x, highAt.y), 0.0).rgb;
    float3 d = SkyShadow.SampleLevel(SkyShadowSampler, highAt, 0.0).rgb;
    return low.y * (low.x * a + high.x * b) + high.y * (low.x * c + high.x * d);
}
#endif

// The share of the key light's light reaching a camera-relative position through the sky, by colour; all of it for a
// light alone, or where the frame draws no sky shadow.
float3 SkyShadowAt(float3 position)
{
#if defined(HAS_SkyShadow)
    [branch] if (KeyLightThroughSky == 0u)
    {
        return float3(1.0, 1.0, 1.0);
    }

    float3 towardLight = -KeyLightDirection;
    float2 onPlane = position.xz - towardLight.xz * (position.y / max(towardLight.y, SkyShadowLowestLight));
    return SkyShadowSmooth(SkyShadowUv(CameraPosition.xz + onPlane));
#else
    return float3(1.0, 1.0, 1.0);
#endif
}

#endif
