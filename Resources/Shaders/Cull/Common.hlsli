// What every culling pass shares: the visibility tests, all in camera-relative view space so they hold up
// far from the origin, the depth pyramid's layout, and how a survivor is appended to its draw. Perspective
// only: an orthographic view sets IsOrthographic and everything passes.

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

// Projected radius in pixels: radius * P22 * height / 2 over the view depth.
float ScreenRadius(float3 center, float radius)
{
    return radius * ProjScale.y * ViewSize.y * 0.5 / max(center.z, Near);
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

// A survivor takes the next instanceCount of a bucket's draw arguments and puts its slot at the bucket's
// firstInstance plus that, in the visible list the vertex stage reads through the instance-rate buffer.
void AppendToBucket(
    RWStructuredBuffer<GpuDrawArgs> drawArgs,
    RWStructuredBuffer<GpuVisible> visibleIds,
    uint bucket,
    uint slot,
    float lodFade)
{
    uint index;
    InterlockedAdd(drawArgs[bucket].instanceCount, 1u, index);

    GpuVisible visible;
    visible.slot = slot;
    visible.lodFade = lodFade;
    visibleIds[drawArgs[bucket].firstInstance + index] = visible;
}

// A survivor of the given bucket is drawn in the bucket its size on screen asks for: its own, or that of a
// lesser version of its mesh once it is small enough. It never switches from one to the next: from a
// threshold down to LOD_BLEND of it further, it is drawn in both, the finer one dithered out as the lesser one
// is dithered in (GpuVisible.lodFade), so every bucket a mesh's versions draw in has room for all its
// instances (Instancing.Group). The early and the late pass both ask, with the same sphere and constants, so
// they agree. An orthographic view keeps the full mesh.
void AppendVisible(
    RWStructuredBuffer<GpuDrawArgs> drawArgs,
    RWStructuredBuffer<GpuVisible> visibleIds,
    StructuredBuffer<GpuLodRow> lods,
    uint bucket,
    uint slot,
    float3 center,
    float radius)
{
    GpuLodRow row = lods[bucket];
    float height = ScreenRadius(center, radius) * 2.0 / ViewSize.y * LodBias;
    bool isPerspective = IsOrthographic == 0u;

    // The thresholds fall from x to z, so each one passed makes the bucket picked so far the finer one.
    uint finer = bucket;
    uint picked = bucket;
    float threshold = 0.0;
    [branch] if (isPerspective && row.count > 0u && height < row.thresholds.x)
    {
        picked = row.group1;
        threshold = row.thresholds.x;
    }

    [branch] if (isPerspective && row.count > 1u && height < row.thresholds.y)
    {
        finer = picked;
        picked = row.group2;
        threshold = row.thresholds.y;
    }

    [branch] if (isPerspective && row.count > 2u && height < row.thresholds.z)
    {
        finer = picked;
        picked = row.group3;
        threshold = row.thresholds.z;
    }

    // How much of the finer version is left: all of it at the threshold, none LOD_BLEND of the threshold under.
    float blendHeight = max(threshold * LOD_BLEND, 1e-6);
    float finerShare = picked != finer ? saturate((height - threshold + blendHeight) / blendHeight) : 0.0;
    [branch] if (finerShare > 0.0)
    {
        AppendToBucket(drawArgs, visibleIds, finer, slot, finerShare);
        AppendToBucket(drawArgs, visibleIds, picked, slot, -finerShare);
    }
    else
    {
        AppendToBucket(drawArgs, visibleIds, picked, slot, 1.0);
    }
}

#endif
