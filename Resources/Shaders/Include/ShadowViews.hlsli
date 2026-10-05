// The shadow maps as the GPU plans and reads them: one depth atlas, the sun's cascades along its top row and the
// pages of the local lights under them, and one GpuShadowView row per cascade or face (Include/Structs.hlsli),
// written by the shadow planning passes (Shadows/ShadowPlan.comp, Shadows/LocalShadowSelect.comp) and read by
// the cull, the draw and the lighting. Everything about where a view's tile sits and what its matrices are is
// here, so no two shaders disagree. Matrices are built for mul(M, v) with v a column: the same convention the
// CPU's untransposed System.Numerics matrices land in (Include/Bindings.hlsli).

#ifndef MAGIC_SHADOW_VIEWS_HLSLI
#define MAGIC_SHADOW_VIEWS_HLSLI

#include "Include/Structs.hlsli"

// Rows 0 .. ShadowMaxCascades - 1 are the main view's cascades; slot s of the local lights holds the six rows
// after them, one per face (a spot light uses the first).
static const uint ShadowMaxCascades = 4u;
static const uint ShadowFacesPerSlot = 6u;
static const uint ShadowMaxSlots = 16u;
static const uint ShadowMaxRows = ShadowMaxCascades + ShadowMaxSlots * ShadowFacesPerSlot;

// GpuShadowView.flags.x
static const uint ShadowViewUsed = 1u;
static const uint ShadowViewOrthographic = 2u;

// GpuShadowHeader.flags
static const uint ShadowSunFlag = 1u;      // the sun casts: the cascades are there to read
static const uint ShadowLocalFlag = 2u;    // some local light holds pages
static const uint ShadowClearAllFlag = 4u; // the layout changed this frame: every tile is drawn again

// GpuLight.shadow.x when the light casts none; GpuLight.shadow.z bit when it may.
static const uint ShadowNone = 0xFFFFFFFFu;
static const uint LightCastsShadows = 1u;

// How near a local light's shadow view starts, in metres (twin: LocalShadows.Near).
static const float ShadowLocalNear = 0.05;

// A point light's faces look down each world axis both ways with a right-angle cone, slightly wider so the
// filter's taps at a face's edge land inside it; a spot's view is its cone plus a margin (twin: LocalShadows).
static const float ShadowPointFaceFovDegrees = 91.5;
static const float ShadowSpotMarginDegrees = 4.0;

uint ShadowFaceRow(uint slot, uint face)
{
    return ShadowMaxCascades + slot * ShadowFacesPerSlot + face;
}

// Where a cascade's tile sits in the atlas, in texels: x origin, y origin, width, height.
float4 CascadeTileTexels(uint cascade, uint resolution)
{
    return float4((float)(cascade * resolution), 0.0, (float)resolution, (float)resolution);
}

// Where a page sits: the grid under the cascade row, as many columns as fit the atlas's width.
float4 PageTexels(uint page, uint pageSize, uint cascadeResolution, uint atlasWidth)
{
    uint columns = max(1u, atlasWidth / max(1u, pageSize));
    return float4((float)((page % columns) * pageSize), (float)(cascadeResolution + (page / columns) * pageSize), (float)pageSize, (float)pageSize);
}

float4 TexelsToUv(float4 texels, float2 atlasSize)
{
    return texels / atlasSize.xyxy;
}

// The view of an orthonormal basis: mul(M, v) gives (dot(v, right), dot(v, up), dot(v, forward)).
float4x4 RotationOf(float3 right, float3 up, float3 forward)
{
    return float4x4(float4(right, 0.0), float4(up, 0.0), float4(forward, 0.0), float4(0.0, 0.0, 0.0, 1.0));
}

// The rotation-only view of a light shining along direction: up is the world axis least along it (twin:
// Cascades.LightRotation).
float4x4 LightRotation(float3 direction)
{
    float3 forward = normalize(direction);
    float3 up = abs(forward.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 right = normalize(cross(up, forward));
    up = cross(forward, right);
    return RotationOf(right, up, forward);
}

// Orthographic, reverse-Z: 1 at near, 0 at far (twin: MatrixExtensions.OrthographicReverseZ).
float4x4 OrthographicReverseZ(float width, float height, float near, float far)
{
    float range = far - near;
    return float4x4(
        2.0 / width, 0.0, 0.0, 0.0,
        0.0, 2.0 / height, 0.0, 0.0,
        0.0, 0.0, -1.0 / range, far / range,
        0.0, 0.0, 0.0, 1.0);
}

// Square perspective, reverse-Z with a finite far (twin: MatrixExtensions.PerspectiveReverseZ with aspect 1).
float4x4 PerspectiveReverseZ(float tanHalf, float near, float far)
{
    float h = 1.0 / tanHalf;
    float range = far - near;
    return float4x4(
        h, 0.0, 0.0, 0.0,
        0.0, h, 0.0, 0.0,
        0.0, 0.0, -near / range, near * far / range,
        0.0, 0.0, 1.0, 0.0);
}

// The face of a six-faced shadow that a direction from the light falls in: +X, -X, +Y, -Y, +Z, -Z.
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

// The direction face f of a point light looks along, in ShadowFaceOf's order.
float3 ShadowFaceForward(uint face)
{
    float3 forward = float3(0.0, 0.0, face == 4u ? 1.0 : -1.0);
    [flatten] if (face < 2u)
        forward = float3(face == 0u ? 1.0 : -1.0, 0.0, 0.0);
    else if (face < 4u)
        forward = float3(0.0, face == 2u ? 1.0 : -1.0, 0.0);
    return forward;
}

// Whether a point is a point light: its cone never ends (twin: GpuLight.From).
bool IsPointLight(GpuLight light)
{
    return light.directionOuterCos.w < -1.5;
}

// A quantised copy of a vector, so a light that has not moved keeps the same key from frame to frame.
float3 ShadowQuantize(float3 value)
{
    return round(value * 1000.0);
}

#endif
