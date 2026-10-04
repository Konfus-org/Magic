// What the lighting passes share: how big a light is on screen and what that does to it, the box of a
// screen tile between two depths and whether a light reaches it, and how a tile's depth is cut into slices.
//
// A light whose reach is a pixel or two across lights nothing anyone can see, so it is left out of the tiles
// and faded out before that, the way an instance too small on screen is not drawn. Perspective only: an
// orthographic view keeps every light.

#ifndef MAGIC_LIGHTING_COMMON_HLSLI
#define MAGIC_LIGHTING_COMMON_HLSLI

#include "Include/GBuffer.hlsli"
#include "Include/Structs.hlsli"

// A light whose range projects to a smaller radius than this, in pixels, is not binned; from twice this
// down to it the light fades out, so it never pops. Higher drops far lights sooner: fewer lights in the far
// tiles, where a tile covers the most ground, but a light's glow goes while it could still be made out.
static const float LightMinPixels = 2.0;

// A light's centre in view space.
float3 LightViewCenter(GpuLight light)
{
    return mul(View, float4(light.positionRange.xyz - CameraPos, 0.0)).xyz;
}

// How much of the light is left for its size on screen: 1 from twice LightMinPixels up, 0 at LightMinPixels.
// Every pass asks with the same light and constants, so a light the cull leaves out is one the shading
// would have given nothing.
float LightScreenFade(GpuLight light)
{
    float pixels = light.positionRange.w * ProjScale.y * ViewSize.y * 0.5 / max(LightViewCenter(light).z, Near);
    float fade = saturate(pixels / LightMinPixels - 1.0);
    return IsOrthographic != 0u ? 1.0 : fade;
}

// The view-space box around a screen tile's part of the view volume between two view depths: its corners
// at both. A perspective slice is widest at its far end on the side away from the view's axis, which the
// min and max pick. firstPixel is the tile's top left, in the view's pixels.
void LightTileBox(uint2 firstPixel, float nearDepth, float farDepth, out float3 boxMin, out float3 boxMax)
{
    float2 lastPixel = float2(min(firstPixel + LightTileSize, (uint2)ViewSize));
    float3 topLeftNear = ViewPosition(float2(firstPixel), nearDepth);
    float3 topLeftFar = ViewPosition(float2(firstPixel), farDepth);
    float3 bottomRightNear = ViewPosition(lastPixel, nearDepth);
    float3 bottomRightFar = ViewPosition(lastPixel, farDepth);
    boxMin = float3(min(topLeftNear.x, topLeftFar.x), min(bottomRightNear.y, bottomRightFar.y), nearDepth);
    boxMax = float3(max(bottomRightNear.x, bottomRightFar.x), max(topLeftNear.y, topLeftFar.y), farDepth);
}

// Whether the light's range reaches into the box.
bool LightTouchesBox(GpuLight light, float3 boxMin, float3 boxMax)
{
    float3 center = LightViewCenter(light);
    float3 toBox = center - clamp(center, boxMin, boxMax);
    float range = light.positionRange.w;
    return dot(toBox, toBox) <= range * range;
}

// A point or spot light's colour as it arrives along lightToSurface (not normalised): inverse-square, eased
// to nothing at the light's range so the edge of its reach is never a visible line, and for a spot faded
// from the inner cone out to the outer one; and faded out as its whole reach gets too small on screen to see.
float3 Arriving(GpuLight light, float3 lightToSurface)
{
    float distanceSquared = dot(lightToSurface, lightToSurface);
    float range = light.positionRange.w;
    float reach = distanceSquared / max(range * range, 1e-6);
    float window = saturate(1.0 - reach * reach);
    float falloff = window * window / (distanceSquared + 1.0);

    float cosAngle = dot(NormalizeOrZero(lightToSurface), light.directionOuterCos.xyz);
    float innerCos = light.colorInnerCos.w;
    float outerCos = light.directionOuterCos.w;
    float cone = saturate((cosAngle - outerCos) / max(innerCos - outerCos, 1e-4));

    return light.colorInnerCos.rgb * (falloff * cone * LightScreenFade(light));
}

// How many times as deep the far end of a tile's drawn depth is as its near end, as a logarithm; never zero,
// so a tile that is all at one depth has one slice that holds it.
float LightSliceSpan(float tileNear, float tileFar)
{
    return max(log(tileFar / tileNear), 1e-6);
}

// The slice of its tile a view depth is in: even steps of its logarithm from the nearest to the farthest
// depth drawn in the tile.
uint LightSlice(float viewDepth, float tileNear, float tileFar)
{
    float along = log(max(viewDepth, tileNear) / tileNear) / LightSliceSpan(tileNear, tileFar);
    return min((uint)(along * (float)LightSlices), LightSlices - 1u);
}

// The view depth a slice of a tile starts at; asked for the slice after the last, where the tile's depth ends.
float LightSliceNearEdge(uint slice, float tileNear, float tileFar)
{
    return tileNear * exp(LightSliceSpan(tileNear, tileFar) * (float)slice / (float)LightSlices);
}

#endif
