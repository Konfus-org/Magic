// Each pixel's shadow mask: a ray from its surface towards the sun, and one towards each of the first lights its tile
// lists, each aimed at a random point of the light so that a wider light casts a softer shadow; 1 where the ray reaches
// the light and 0 where something is in the way. The pass header's x is the frame's constants and z the lighting
// constants.
module "TraceShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Lighting;
import Scene.Lights;
import Scene.Shadows;
import Light.Lights;

// The sun's angular radius as it looks from the Earth.
static const float SunAngularRadius = 0.00465;

// How many pixels' width a ray starts off its surface, beyond a centimetre: the drawn cut of a model's clusters may
// stand that far from the full detail rays hit.
static const float OffsetPixels = 4.0;

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

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceShadows(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(thread.xy, 0u) == 0u)
        return;

    if (frame.acceleration == 0u)
    {
        StoreShadowMask(lighting.shadowMask, thread.xy, size.x, uint4(0xFFFFFFFFu));
        return;
    }

    float2 uv = GetTexelCenterUv(thread.xy, size);
    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    float3 relative = ReconstructRelative(frame, uv, depthTexture.Load(thread.xy, 0u).x);
    float3 normal = DecodeOctahedral(normalTexture.Load(thread.xy, 0u).xy * 2.0 - 1.0);
    float3 world = relative + frame.cameraPosition.xyz;
    float offset = 0.01 + OffsetPixels * length(relative) / frame.pixelsPerRadian;
    float3 origin = world + normal * offset;
    AccelerationRef acceleration = { frame.acceleration };
    RaytracingAccelerationStructure scene = acceleration.Get();

    // The sun, through a random point of its disc.
    uint4 words = uint4(0u);
    float3 toSun = frame.sunDirection.xyz;
    float2 disc = SampleConcentricDisk(GetRandom(thread.xy, frame.frameIndex, 0u).xy) * SunAngularRadius;
    float3 sunRay = normalize(ToWorld(CreateBasis(toSun), float3(disc, 1.0)));
    if (dot(normal, toSun) > 0.0 && CanReach(scene, origin, sunRay, 1e6))
        words = EncodeShadow(words, 0u, 1.0);

    // The tile's lights in the order shading reads them, until the slots run out.
    ArrayRef<uint> tiles = { lighting.lightTiles };
    uint tilesAcross = (size.x + LightTileSize - 1u) / LightTileSize;
    uint tile = GetLightTileOffset(thread.xy / LightTileSize, tilesAcross);
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
            float3 target = SampleLightPoint(light, world, GetRandom(thread.xy, frame.frameIndex, slot).xy);
            float3 toLight = target - origin;
            float distance = length(toLight);
            float3 direction = toLight / max(distance, Epsilon);
            if (dot(normal, direction) > 0.0 && CanReach(scene, origin, direction, distance * 0.999))
                words = EncodeShadow(words, slot, 1.0);

            slot++;
        }
    }

    StoreShadowMask(lighting.shadowMask, thread.xy, size.x, words);
}
