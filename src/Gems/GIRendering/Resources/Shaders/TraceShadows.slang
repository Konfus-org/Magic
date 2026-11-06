// Each pixel's traced shadow mask: a ray from its surface towards the sun, and one towards each of the first lights its
// tile lists, each aimed at a random point of the light so that a wider light casts a softer shadow; 1 where the ray
// reaches the light and 0 where something is in the way. The sun's rays also go into its tiles, for the denoiser. The
// pass header's x is the frame's constants and z the lighting constants.
module "TraceShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Instances;
import Scene.Visibility;
import Scene.Lighting;
import Scene.Lights;
import Scene.Shadows;
import Light.Lights;

// The sun's angular radius as it looks from the Earth.
static const float SunAngularRadius = 0.00465;

// How many pixels' width a ray starts off its surface, beyond a centimetre: the drawn cut of a model's clusters may
// stand that far from the full detail rays hit.
static const float OffsetPixels = 4.0;

// The sun's bits of the group's two tiles, the upper and the lower 8 by 4 pixels.
groupshared uint groupSunTiles[2];

// Whether nothing is in the way along a ray up to a distance.
bool CanReach(RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance)
{
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, RAY_FLAG_NONE, 0xFFu, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_NOTHING;
}

// How far a point must rise along a vertex's normal to reach the plane through the vertex square to it, when it lies
// below that plane.
float3 GetLift(float3 fromVertex, float3 normal) { return -min(0.0, dot(fromVertex, normal)) * normal; }

// A pixel's point lifted off its flat triangle towards the curved surface the triangle's vertex normals describe, so
// that a ray from the lit side of a curve doesn't hit the next triangle and shadow the curve a facet at a time (Hanika,
// "Hacking the Shadow Terminator", Ray Tracing Gems II, 2021). A point in a hollow stays where it is.
float3 LiftOffTriangle(FrameConstants frame, uint id, float2 uv, float3 relative)
{
    uint visibleIndex;
    uint triangle;
    DecodeVisibilityId(id, visibleIndex, triangle);
    PixelTriangle shown = LoadPixelTriangle(frame, visibleIndex, triangle, ToClip(uv));
    float3 lambda = shown.bary.lambda;
    return relative
        + lambda.x * GetLift(relative - shown.p0, ToWorldNormal(shown.instance.world, shown.v0.normal))
        + lambda.y * GetLift(relative - shown.p1, ToWorldNormal(shown.instance.world, shown.v1.normal))
        + lambda.z * GetLift(relative - shown.p2, ToWorldNormal(shown.instance.world, shown.v2.normal));
}

// A pixel's words: all lit for the sky and where there is nothing to hit.
uint4 Trace(FrameConstants frame, LightingConstants lighting, uint2 pixel)
{
    uint2 size = uint2(frame.width, frame.height);
    UintTextureRef visibility = { frame.visibility };
    uint id = visibility.Load(pixel, 0u);
    if (id == 0u || frame.acceleration == 0u)
        return uint4(0xFFFFFFFFu);

    float2 uv = GetTexelCenterUv(pixel, size);
    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    float3 relative = ReconstructRelative(frame, uv, depthTexture.Load(pixel, 0u).x);
    float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    float3 world = LiftOffTriangle(frame, id, uv, relative) + frame.cameraPosition.xyz;
    float offset = 0.01 + OffsetPixels * length(relative) / frame.pixelsPerRadian;
    float3 origin = world + normal * offset;
    AccelerationRef acceleration = { frame.acceleration };
    RaytracingAccelerationStructure scene = acceleration.Get();

    // The sun, through a random point of its disc.
    uint4 words = uint4(0u);
    float3 toSun = frame.sunDirection.xyz;
    float2 disc = SampleConcentricDisk(GetRandom(pixel, frame.frameIndex, 0u).xy) * SunAngularRadius;
    float3 sunRay = normalize(ToWorld(CreateBasis(toSun), float3(disc, 1.0)));
    if (dot(normal, toSun) > 0.0 && CanReach(scene, origin, sunRay, 1e6))
        words = EncodeShadow(words, 0u, 1.0);

    // The tile's lights in the order shading reads them, until the slots run out.
    ArrayRef<uint> tiles = { lighting.lightTiles };
    uint tilesAcross = (size.x + LightTileSize - 1u) / LightTileSize;
    uint tile = GetLightTileOffset(pixel / LightTileSize, tilesAcross);
    uint buckets = (min(lighting.lightCount, MaxLights) + 31u) / 32u;
    uint slot = 1u;
    for (uint bucket = 0u; bucket < buckets && slot < ShadowSlots; bucket++)
    {
        uint bits = tiles[tile + bucket];
        while (bits != 0u && slot < ShadowSlots)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1u;
            GpuLight light = LoadLight(lighting.lights, bucket * 32u + bit);
            float3 target = SampleLightPoint(light, world, GetRandom(pixel, frame.frameIndex, slot).xy);
            float3 toLight = target - origin;
            float distance = length(toLight);
            float3 direction = toLight / max(distance, Epsilon);
            if (dot(normal, direction) > 0.0 && CanReach(scene, origin, direction, distance * 0.999))
                words = EncodeShadow(words, slot, 1.0);

            slot++;
        }
    }

    return words;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    uint2 size = uint2(frame.width, frame.height);
    if (local.x == 0u && local.y < 2u)
        groupSunTiles[local.y] = 0u;

    GroupMemoryBarrierWithGroupSync();
    bool isInside = all(thread.xy < size);
    if (isInside)
    {
        uint4 words = Trace(frame, lighting, thread.xy);
        StoreShadowMask(lighting.tracedShadows, thread.xy, size.x, words);
        if (GetShadow(words, 0u) > 0.5)
            InterlockedOr(groupSunTiles[local.y / SunTileSize.y], GetSunTileBit(thread.xy));
    }

    GroupMemoryBarrierWithGroupSync();
    if (isInside && local.x == 0u && local.y % SunTileSize.y == 0u)
    {
        ArrayRef<uint> sunTiles = { lighting.sunTiles };
        sunTiles[GetSunTileIndex(thread.xy / SunTileSize, size.x)] = groupSunTiles[local.y / SunTileSize.y];
    }
}
