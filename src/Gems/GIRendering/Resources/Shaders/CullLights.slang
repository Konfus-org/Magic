// Each tile of the screen's lights: the depths its pixels hold bound a frustum, and a light whose sphere is inside the
// frustum's sides and touches the box around it sets its bit in the tile's words (Harada et al., "Forward+: Bringing
// Deferred Lighting to the Next Level", 2012). The pass header's x is the frame's constants and z the lighting
// constants.
module "CullLights";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Lighting;
import Scene.Lights;

static const uint Threads = 8u;
// The pixels each thread reads the depth of, along a side.
static const uint Granularity = LightTileSize / Threads;

groupshared uint nearestDepth;
groupshared uint farthestDepth;
groupshared uint tileBits[LightBuckets];

struct Plane
{
    float3 normal;
    float offset;
}

// The plane through three points, its normal by their winding.
Plane CreatePlane(float3 p0, float3 p1, float3 p2)
{
    Plane plane;
    plane.normal = normalize(cross(p1 - p0, p2 - p0));
    plane.offset = dot(plane.normal, p0);
    return plane;
}

bool IsSphereOutside(Plane plane, float3 center, float radius) { return dot(plane.normal, center) - plane.offset < -radius; }

bool IsSphereTouchingBox(float3 center, float radius, float3 low, float3 high)
{
    float3 outside = max(max(low - center, center - high), 0.0);
    return dot(outside, outside) <= radius * radius;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CullLights(uint3 group : SV_GroupID, uint3 thread : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    uint2 size = uint2(frame.width, frame.height);
    for (uint i = index; i < LightBuckets; i += Threads * Threads)
    {
        tileBits[i] = 0u;
    }

    if (index == 0u)
    {
        nearestDepth = 0u;
        farthestDepth = 0xFFFFFFFFu;
    }

    GroupMemoryBarrierWithGroupSync();

    // Reversed depth is positive, so its bits order as the depths do: the nearest is the greatest.
    TextureRef depthTexture = { frame.depth };
    float nearest = 0.0;
    float farthest = 1.0;
    for (uint j = 0u; j < Granularity * Granularity; j++)
    {
        uint2 pixel = min(thread.xy * Granularity + uint2(j % Granularity, j / Granularity), size - 1u);
        float depth = depthTexture.Load(pixel, 0u).x;
        nearest = max(nearest, depth);
        farthest = min(farthest, depth);
    }

    InterlockedMax(nearestDepth, asuint(nearest));
    InterlockedMin(farthestDepth, asuint(farthest));
    GroupMemoryBarrierWithGroupSync();

    // A tile of nothing but sky lights nothing.
    float tileNear = asfloat(nearestDepth);
    float tileFar = max(asfloat(farthestDepth), 1e-7);
    if (tileNear > 0.0)
    {
        // The tile's corners at its nearest and farthest depth, in the camera-relative world.
        float3 corners[8];
        for (uint c = 0u; c < 8u; c++)
        {
            uint2 corner = min((group.xy + uint2(c & 1u, (c >> 1u) & 1u)) * LightTileSize, size);
            float2 uv = float2(corner) / float2(size);
            corners[c] = ReconstructRelative(frame, uv, c < 4u ? tileNear : tileFar);
        }

        // The four sides, each through the camera and two near corners, so a tile one depth deep still has them.
        float3 eye = float3(0.0);
        Plane sides[4];
        sides[0] = CreatePlane(eye, corners[2], corners[0]);
        sides[1] = CreatePlane(eye, corners[1], corners[3]);
        sides[2] = CreatePlane(eye, corners[0], corners[1]);
        sides[3] = CreatePlane(eye, corners[3], corners[2]);
        float3 low = corners[0];
        float3 high = corners[0];
        float3 middle = corners[0];
        for (uint k = 1u; k < 8u; k++)
        {
            low = min(low, corners[k]);
            high = max(high, corners[k]);
            middle += corners[k];
        }

        // The planes' facing depends on the corners' winding, which the projection's handedness decides: the corners'
        // centroid is inside them all, so a plane it is outside of is flipped.
        middle /= 8.0;
        for (uint p = 0u; p < 4u; p++)
        {
            if (dot(sides[p].normal, middle) - sides[p].offset < 0.0)
            {
                sides[p].normal = -sides[p].normal;
                sides[p].offset = -sides[p].offset;
            }
        }

        uint count = min(lighting.lightCount, MaxLights);
        for (uint l = index; l < count; l += Threads * Threads)
        {
            GpuLight light = LoadLight(lighting.lights, l);
            float3 center = Relative(frame, light.position);
            float radius = GetLightReach(light);
            bool isOutside = false;
            for (uint s = 0u; s < 4u; s++)
            {
                isOutside = isOutside || IsSphereOutside(sides[s], center, radius);
            }

            if (!isOutside && IsSphereTouchingBox(center, radius, low, high))
                InterlockedOr(tileBits[l / 32u], 1u << (l % 32u));
        }
    }

    GroupMemoryBarrierWithGroupSync();
    uint tilesAcross = (size.x + LightTileSize - 1u) / LightTileSize;
    ArrayRef<uint> tiles = { lighting.lightTiles };
    uint offset = GetLightTileOffset(group.xy, tilesAcross);
    for (uint b = index; b < LightBuckets; b += Threads * Threads)
    {
        tiles[offset + b] = tileBits[b];
    }
}
