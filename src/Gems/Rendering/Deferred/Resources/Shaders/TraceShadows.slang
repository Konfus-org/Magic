// Each pixel's traced shadow mask: a ray from its surface towards the sun, and one towards each of the first lights its
// tile lists, each aimed at a random point of the light so that a wider light casts a softer shadow; how much of the
// light gets through, 1 where nothing is in the way and 0 past something opaque. The sun's rays also go into its tiles,
// for the denoiser, one bit a pixel: lit at random as often as the light gets through; and the colour its light takes
// on through glass into the sun's tint. The pass header's x is the frame's buffer, which holds the lights and the
// shadows.
module "TraceShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Scene.Shadows;
import Light.Lights;
import Trace.HitSurface;
import Scene.Models;

// The sun's bits of the group's two tiles, the upper and the lower 8 by 4 pixels.
groupshared uint groupSunTiles[2];

// A pixel's words, all lit for the sky and where there is nothing to hit, and the sun's tint, white unless its ray
// went through glass.
uint4 Trace(FrameConstants frame, Lights lights, uint2 pixel, out float3 sunTint)
{
    Models models = GetModels();
    sunTint = float3(1.0);
    uint2 size = uint2(frame.width, frame.height);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0 || models.acceleration == 0u)
        return uint4(0xFFFFFFFFu);

    // The point lifted off its flat triangle towards the curve its vertex normals describe, as the resolve found it,
    // so that a ray from the lit side of a curve doesn't shadow it a facet at a time.
    float2 uv = GetTexelCenterUv(pixel, size);
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float3 relative = ReconstructRelative(frame, uv, depth);
    float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    float lift = DecodeLift(frame, relative, surfaceTexture.Load(pixel, 0u).w);
    float3 world = relative + normal * lift + frame.cameraPosition.xyz;
    float3 origin = world + normal * GetRayOffset(frame, relative);
    AccelerationRef acceleration = { models.acceleration };
    RaytracingAccelerationStructure scene = acceleration.Get();

    // The sun, through a random point of its disc, lit as often as its light gets through, its colour over its
    // brightness the tint.
    uint4 words = uint4(0u);
    float3 toSun = lights.sunDirection.xyz;
    float3 xi = GetRandom(pixel, frame.frameIndex, 0u);
    float2 disc = SampleConcentricDisk(xi.xy) * SunAngularRadius;
    float3 sunRay = normalize(ToWorld(CreateBasis(toSun), float3(disc, 1.0)));
    float3 sunThrough = dot(normal, toSun) > 0.0 ? GetTransmittance(scene, origin, sunRay, 1e6) : float3(0.0);
    float sunBrightness = GetLuminance(sunThrough);
    if (sunBrightness > xi.z)
        words = EncodeShadow(words, 0u, 1.0);

    if (sunBrightness > 0.0)
        sunTint = sunThrough / sunBrightness;

    // The tile's lights in the order shading reads them, until the slots run out.
    ArrayRef<uint> tiles = { lights.tiles };
    uint tilesAcross = (size.x + LightTileSize - 1u) / LightTileSize;
    uint tile = GetLightTileOffset(pixel / LightTileSize, tilesAcross);
    uint buckets = (min(lights.count, MaxLights) + 31u) / 32u;
    uint slot = 1u;
    for (uint bucket = 0u; bucket < buckets && slot < ShadowSlots; bucket++)
    {
        uint bits = tiles[tile + bucket];
        while (bits != 0u && slot < ShadowSlots)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1u;
            GpuLight light = LoadLight(lights.table, bucket * 32u + bit);
            float3 target = SampleLightPoint(light, world, GetRandom(pixel, frame.frameIndex, slot).xy);
            float3 toLight = target - origin;
            float distance = length(toLight);
            float3 direction = toLight / max(distance, Epsilon);
            if (dot(normal, direction) > 0.0)
            {
                float through = GetLuminance(GetTransmittance(scene, origin, direction, distance * 0.999));
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
    Lights lights = GetLights();
    Shadows shadows = GetShadows();
    uint2 size = uint2(frame.width, frame.height);
    if (local.x == 0u && local.y < 2u)
        groupSunTiles[local.y] = 0u;

    GroupMemoryBarrierWithGroupSync();
    bool isInside = all(thread.xy < size);
    if (isInside)
    {
        float3 sunTint;
        uint4 words = Trace(frame, lights, thread.xy, sunTint);
        StoreShadowMask(shadows.traced, thread.xy, size.x, words);
        StorageRef tints = { shadows.sunTint, 0u };
        tints.Store(thread.xy, float4(sunTint, 1.0));
        if (GetShadow(words, 0u) > 0.5)
            InterlockedOr(groupSunTiles[local.y / SunTileSize.y], GetSunTileBit(thread.xy));
    }

    GroupMemoryBarrierWithGroupSync();
    if (isInside && local.x == 0u && local.y % SunTileSize.y == 0u)
    {
        ArrayRef<uint> sunTiles = { shadows.sunTiles };
        sunTiles[GetSunTileIndex(thread.xy / SunTileSize, size.x)] = groupSunTiles[local.y / SunTileSize.y];
    }
}
