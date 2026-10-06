// What every culling pass shares: the visibility tests, all in camera-relative view space so they hold up
// far from the origin, the depth pyramid's layout, and how a survivor is appended to its draw. The camera's
// fast tests are for its symmetric perspective (an orthographic camera sets IsOrthographic and passes them);
// the shadow views cull against six planes taken from their ViewProj, which fit any projection.

#ifndef MAGIC_CULL_COMMON_HLSLI
#define MAGIC_CULL_COMMON_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Structs.hlsli"

// Thread-group sizes the CPU and more than one shader agree on. Macros because numthreads needs them.
#define INSTANCES_PER_PAGE 256   // CullEarly: one group per page (InstanceTable.PageSize)
#define CANDIDATES_PER_GROUP 256 // CullLate's group, and SeedLateArgs' division into groups

// The share of a LOD threshold over which an instance blends from one version of its mesh into the next.
// Culling.LodBlend, which the CPU defines when it compiles the cull shaders; without it nothing blends.
#ifndef LOD_BLEND
#define LOD_BLEND 0.0
#endif

// A world-space point in view space: relative to the camera, rotated by the view matrix.
float3 ToView(float3 world)
{
    return mul(View, float4(world - CameraPos, 0.0)).xyz;
}

// A world-space direction in view space: the rotation alone.
float3 ToViewDirection(float3 direction)
{
    return mul(View, float4(direction, 0.0)).xyz;
}

// How far inside each side plane of the symmetric perspective a view-space point is (left, right, bottom,
// top), scaled by the length of that plane's normal: the normals are (-P11, 0, 1), (P11, 0, 1),
// (0, -P22, 1), (0, P22, 1) and point inwards, so a component >= 0 means inside. Linear in the point.
float4 SidePlaneDistances(float3 viewPoint)
{
    float2 scaled = ProjScale * viewPoint.xy;
    return viewPoint.z + float4(-scaled.x, scaled.x, -scaled.y, scaled.y);
}

// Inside or touching the view volume: the four side planes and the near plane. The radius is scaled by
// the normals' lengths instead of normalising the distances, so no plane is normalised per instance.
bool SphereInFrustum(float3 center, float radius)
{
    float2 normalLength = sqrt(ProjScale * ProjScale + 1.0);
    bool isInside = all(SidePlaneDistances(center) >= -radius * normalLength.xxyy) && center.z + radius >= Near;
    return IsOrthographic != 0u || isInside;
}

// An axis-aligned world box against the same planes: culled when all eight corners are outside one of
// them. Every test is linear, so the largest value over the corners is the value at the minimum corner
// plus each edge's contribution where that is positive; no corner is ever built.
bool BoxInFrustum(float3 boxMin, float3 boxMax)
{
    float3 origin = ToView(boxMin);
    float3 extent = boxMax - boxMin;
    float3 edgeX = ToViewDirection(float3(extent.x, 0.0, 0.0));
    float3 edgeY = ToViewDirection(float3(0.0, extent.y, 0.0));
    float3 edgeZ = ToViewDirection(float3(0.0, 0.0, extent.z));

    float4 sides = SidePlaneDistances(origin)
        + max(SidePlaneDistances(edgeX), 0.0)
        + max(SidePlaneDistances(edgeY), 0.0)
        + max(SidePlaneDistances(edgeZ), 0.0);
    float farthest = origin.z + max(edgeX.z, 0.0) + max(edgeY.z, 0.0) + max(edgeZ.z, 0.0);
    bool isInside = all(sides >= 0.0) && farthest >= Near;
    return IsOrthographic != 0u || isInside;
}

// The six clip planes of ViewProj (Gribb and Hartmann), for camera-relative positions: normals pointing in,
// normalised. mul(ViewProj, v) makes clip = (r0.v, r1.v, r2.v, r3.v) with ri the rows ViewProj[i]; inside is
// -w <= x <= w, -w <= y <= w and, reverse-Z, 0 <= z <= w. An infinite perspective's far plane has a zero
// normal and holds everything, as it should.
struct ClipPlanes
{
    float4 planes[6];
};

ClipPlanes FrustumPlanesOf(float4x4 viewProj)
{
    float4 r0 = viewProj[0], r1 = viewProj[1], r2 = viewProj[2], r3 = viewProj[3];
    ClipPlanes result;
    result.planes[0] = r3 + r0;
    result.planes[1] = r3 - r0;
    result.planes[2] = r3 + r1;
    result.planes[3] = r3 - r1;
    result.planes[4] = r3 - r2;
    result.planes[5] = r2;
    [unroll] for (uint i = 0u; i < 6u; i++)
        result.planes[i] /= max(length(result.planes[i].xyz), 1e-20);
    return result;
}

ClipPlanes FrustumPlanes()
{
    return FrustumPlanesOf(ViewProj);
}

// A camera-relative sphere touches the volume: not wholly outside any plane.
bool SphereInPlanes(ClipPlanes clip, float3 centerRel, float radius)
{
    bool inside = true;
    [unroll] for (uint i = 0u; i < 6u; i++)
        inside = inside && dot(clip.planes[i].xyz, centerRel) + clip.planes[i].w >= -radius;
    return inside;
}

// A camera-relative axis-aligned box touches the volume: its corner farthest along each plane's normal is inside it.
bool BoxInPlanes(ClipPlanes clip, float3 boxMinRel, float3 boxMaxRel)
{
    bool inside = true;
    [unroll] for (uint i = 0u; i < 6u; i++)
    {
        float3 normal = clip.planes[i].xyz;
        float3 farthest = float3(normal.x > 0.0 ? boxMaxRel.x : boxMinRel.x, normal.y > 0.0 ? boxMaxRel.y : boxMinRel.y, normal.z > 0.0 ? boxMaxRel.z : boxMinRel.z);
        inside = inside && dot(normal, farthest) + clip.planes[i].w >= 0.0;
    }

    return inside;
}

// Projected radius in pixels: radius * P22 * height / 2 over the view depth; for an orthographic view, whose P22 is
// 2 over its height in metres, the radius in pixels at any depth.
float ScreenRadius(float3 center, float radius)
{
    float depth = IsOrthographic != 0u ? 1.0 : max(center.z, Near);
    return radius * ProjScale.y * ViewSize.y * 0.5 / depth;
}

// One axis of a view-space sphere's screen box (Mara and McGuire 2013): (c, z) rotated by -+atan(r / t),
// t being the length of the tangent from the camera, gives the two silhouette points; each is projected
// and scaled by the projection's diagonal term. Returns (min, max) in NDC. Only for a sphere wholly past
// the near plane, where t is real and both depths positive.
float2 SilhouetteExtent(float2 center, float radius, float projScale)
{
    float tangentLength = sqrt(dot(center, center) - radius * radius);
    float2 low = float2(tangentLength * center.x - radius * center.y, radius * center.x + tangentLength * center.y);
    float2 high = float2(tangentLength * center.x + radius * center.y, tangentLength * center.y - radius * center.x);
    return float2(low.x / low.y, high.x / high.y) * projScale;
}

// The depth pyramid: level l is HiZSize / 2^l floats a side, rounded up, levels stored one after the other,
// HiZLevelCount of them. Level 0 is half the view's resolution, rounded up, so a texel of level l covers
// exactly the view's 2^(l+1) x 2^(l+1) pixels at 2^(l+1) times its coordinates (past the edge: nothing).
uint2 HiZLevelSize(uint level)
{
    return (HiZSize + (1u << level) - 1u) >> level;
}

uint HiZLevelOffset(uint level)
{
    uint offset = 0u;
    [loop] for (uint lower = 0u; lower < level; lower++)
    {
        uint2 size = HiZLevelSize(lower);
        offset += size.x * size.y;
    }

    return offset;
}

// True when the sphere lies wholly behind what the pyramid recorded over its screen footprint. Reverse-Z:
// the pyramid holds the smallest (farthest) depth in each texel's area, so a sphere whose nearest point is
// still farther than that is hidden. A sphere that crosses the near plane is never hidden: nothing can be
// said about its footprint.
bool IsOccluded(StructuredBuffer<float> hiZ, float3 center, float radius)
{
    bool isHidden = false;
    [branch] if (center.z - radius >= Near)
    {
        // The sphere's screen box in NDC (+Y up), then in texture space (origin top left), clamped to the view.
        float2 extentX = SilhouetteExtent(center.xz, radius, ProjScale.x);
        float2 extentY = SilhouetteExtent(center.yz, radius, ProjScale.y);
        float2 uvMin = saturate(float2(extentX.x * 0.5 + 0.5, 0.5 - extentY.y * 0.5));
        float2 uvMax = saturate(float2(extentX.y * 0.5 + 0.5, 0.5 - extentY.x * 0.5));

        // The level where the box is at most two texels a side, so four taps cover it.
        float2 sizeInTexels = (uvMax - uvMin) * float2(HiZSize);
        uint level = min(HiZLevelCount - 1u, (uint)ceil(log2(max(max(sizeInTexels.x, sizeInTexels.y), 1.0))));
        uint2 levelSize = HiZLevelSize(level);
        uint offset = HiZLevelOffset(level);
        uint2 texelMin = min((uint2)(uvMin * ViewSize) >> (level + 1u), levelSize - 1u);
        uint2 texelMax = min((uint2)(uvMax * ViewSize) >> (level + 1u), levelSize - 1u);

        float farthest = hiZ[offset + texelMin.y * levelSize.x + texelMin.x];
        farthest = min(farthest, hiZ[offset + texelMin.y * levelSize.x + texelMax.x]);
        farthest = min(farthest, hiZ[offset + texelMax.y * levelSize.x + texelMin.x]);
        farthest = min(farthest, hiZ[offset + texelMax.y * levelSize.x + texelMax.x]);

        float nearest = Near / max(center.z - radius, Near); // the sphere's closest point, as reverse-Z depth
        isHidden = nearest < farthest;
    }

    return isHidden;
}

// An instance into a bucket's draw: one more instance, its slot (with the level of detail in its top bits) and fade
// at the next place of the bucket's run of the visible list. argsOffset is where the view's slice of the draw args
// starts (0 for a camera).
void AppendToBucket(
    RWStructuredBuffer<GpuDrawArgs> drawArgs,
    RWStructuredBuffer<GpuVisible> visibleIds,
    uint argsOffset,
    uint bucket,
    uint slot,
    uint level,
    float lodFade)
{
    uint index;
    InterlockedAdd(drawArgs[argsOffset + bucket].instanceCount, 1u, index);

    GpuVisible visible;
    visible.slot = slot | (level << GpuVisibleLevelShift);
    visible.lodFade = lodFade;
    visibleIds[drawArgs[argsOffset + bucket].firstInstance + index] = visible;
}

// A survivor of the given bucket is drawn in the bucket its size on screen asks for: its own, or that of a
// lesser version of its mesh once it is small enough. It never switches from one to the next: from a
// threshold down to LOD_BLEND of it further, it is drawn in both, the finer one dithered out as the lesser one
// is dithered in (GpuVisible.lodFade), so every bucket a mesh's versions draw in has room for all its
// instances (Instancing.Group). height is the instance's height on screen as a share of the view's, scaled by
// the LOD bias: the early and the late pass both ask with the same sphere and constants, so they agree, and a
// shadow view asks in its own texels.
void AppendVisibleHeight(
    RWStructuredBuffer<GpuDrawArgs> drawArgs,
    RWStructuredBuffer<GpuVisible> visibleIds,
    StructuredBuffer<GpuLodRow> lods,
    uint argsOffset,
    uint bucket,
    uint slot,
    float height)
{
    GpuLodRow row = lods[bucket];

    // The thresholds fall from x to w, so each one passed makes the bucket picked so far the finer one.
    uint finer = bucket;
    uint picked = bucket;
    uint finerLevel = 0u;
    uint pickedLevel = 0u;
    float threshold = 0.0;
    [unroll] for (uint level = 0u; level < 4u; level++)
    {
        [branch] if (row.thresholds[level] > 0.0 && height < row.thresholds[level])
        {
            finer = picked;
            finerLevel = pickedLevel;
            picked = row.groups[level];
            pickedLevel = level + 1u;
            threshold = row.thresholds[level];
        }
    }

    // How much of the finer version is left: all of it at the threshold, none LOD_BLEND of the threshold under.
    float blendHeight = max(threshold * LOD_BLEND, 1e-6);
    float finerShare = picked != finer ? saturate((height - threshold + blendHeight) / blendHeight) : 0.0;
    [branch] if (finerShare > 0.0)
    {
        AppendToBucket(drawArgs, visibleIds, argsOffset, finer, slot, finerLevel, finerShare);
        AppendToBucket(drawArgs, visibleIds, argsOffset, picked, slot, pickedLevel, -finerShare);
    }
    else
    {
        AppendToBucket(drawArgs, visibleIds, argsOffset, picked, slot, pickedLevel, 1.0);
    }
}

// The camera's version: the height from the view-space sphere and the frame's projection and LOD bias.
void AppendVisible(
    RWStructuredBuffer<GpuDrawArgs> drawArgs,
    RWStructuredBuffer<GpuVisible> visibleIds,
    StructuredBuffer<GpuLodRow> lods,
    uint bucket,
    uint slot,
    float3 center,
    float radius)
{
    float height = ScreenRadius(center, radius) * 2.0 / ViewSize.y * LodBias;
    AppendVisibleHeight(drawArgs, visibleIds, lods, 0u, bucket, slot, height);
}

#endif
