// Each pixel's traced shadow mask: a ray from its surface towards the sun, and one towards each of the first lights its
// tile lists, each aimed at a random point of the light so that a wider light casts a softer shadow; how much of the
// light gets through, 1 where nothing is in the way and 0 past something opaque. The sun's rays also go into its tiles,
// for the denoiser, one bit a pixel: lit at random as often as the light gets through; and the colour its light takes
// on through glass into the sun's tint. The pass header's x is the frame's constants and z the lighting constants.
module "TraceShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Instances;
import Scene.Visibility;
import Scene.Impostors;
import Scene.Lighting;
import Scene.Lights;
import Scene.Shadows;
import Light.Lights;
import Trace.HitSurface;
import Scene.Clusters;

// The sun's bits of the group's two tiles, the upper and the lower 8 by 4 pixels.
groupshared uint groupSunTiles[2];

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

// A pixel's words, all lit for the sky and where there is nothing to hit, and the sun's tint, white unless its ray
// went through glass.
uint4 Trace(FrameConstants frame, LightingConstants lighting, uint2 pixel, out float3 sunTint)
{
    sunTint = float3(1.0);
    uint2 size = uint2(frame.width, frame.height);
    UintTextureRef visibility = { GetClusters().visibility };
    uint id = visibility.Load(pixel, 0u);
    if (id == 0u || frame.acceleration == 0u)
        return uint4(0xFFFFFFFFu);

    float2 uv = GetTexelCenterUv(pixel, size);
    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    float3 relative = ReconstructRelative(frame, uv, depthTexture.Load(pixel, 0u).x);
    float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    // An impostor's pixel has no triangle to lift off.
    float3 lifted = (id & ImpostorFlag) != 0u ? relative : LiftOffTriangle(frame, id, uv, relative);
    float3 world = lifted + frame.cameraPosition.xyz;
    float3 origin = world + normal * GetRayOffset(frame, relative);
    AccelerationRef acceleration = { frame.acceleration };
    RaytracingAccelerationStructure scene = acceleration.Get();

    // The sun, through a random point of its disc, lit as often as its light gets through, its colour over its
    // brightness the tint.
    uint4 words = uint4(0u);
    float3 toSun = frame.sunDirection.xyz;
    float3 xi = GetRandom(pixel, frame.frameIndex, 0u);
    float2 disc = SampleConcentricDisk(xi.xy) * SunAngularRadius;
    float3 sunRay = normalize(ToWorld(CreateBasis(toSun), float3(disc, 1.0)));
    float3 sunThrough = dot(normal, toSun) > 0.0 ? GetTransmittance(frame, scene, origin, sunRay, 1e6) : float3(0.0);
    float sunBrightness = GetLuminance(sunThrough);
    if (sunBrightness > xi.z)
        words = EncodeShadow(words, 0u, 1.0);

    if (sunBrightness > 0.0)
        sunTint = sunThrough / sunBrightness;

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
            if (dot(normal, direction) > 0.0)
            {
                float through = GetLuminance(GetTransmittance(frame, scene, origin, direction, distance * 0.999));
                words = EncodeShadow(words, slot, through);
            }

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
        float3 sunTint;
        uint4 words = Trace(frame, lighting, thread.xy, sunTint);
        StoreShadowMask(lighting.tracedShadows, thread.xy, size.x, words);
        StorageRef tints = { lighting.sunTint, 0u };
        tints.Store(thread.xy, float4(sunTint, 1.0));
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
