// Each pixel's shadow mask as the rays leave it, which the denoiser then blends in place: a ray from its surface
// towards the sun, and one towards each of the first lights its tile lists, each aimed at a random point of the light
// so that a wider light casts a softer shadow. How much of the light gets through, 1 where nothing is in the way and 0
// past something opaque. The sun's rays also go into its tiles, for the denoiser, one bit a pixel: lit at random as
// often as the light gets through, and the colour its light takes on through glass into the sun's tint. A thread is a
// traced pixel, which traces from the picture's pixel at its footprint's corner. The pass header's x is the frame's
// buffer, which holds the lights, and its y the denoiser's constants, with the traced targets.
module "TraceShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Random;
import Scene.Lights;
import Scene.Shadows;
import Light.LocalLights;
import Trace.HitSurface;
import Scene.Models;
import Light.Indirect;
import DenoiseShadows;

// The sun's bits of the group's two tiles, the upper and the lower 8 by 4 pixels.
groupshared uint groupSunTiles[2];

// A pixel's words, all lit for the sky and where there is nothing to hit, and the sun's tint, white unless its ray
// went through glass.
struct PixelShadows
{
    uint4 words;
    float3 sunTint;
};

PixelShadows Trace(FrameConstants frame, Lights lights, uint2 pixel)
{
    TracedWorld traced = GetTracedWorld();
    PixelShadows shadows = { uint4(0u), float3(1.0) };
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0 || traced.acceleration == 0u)
    {
        shadows.words = uint4(0xFFFFFFFFu);
        return shadows;
    }

    // The point lifted off its flat triangle towards the curve its vertex normals describe, as the resolve found it,
    // so that a ray from the lit side of a curve doesn't shadow it a facet at a time.
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float3 relative = ReconstructPixel(frame, pixel, depth);
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    float lift = DecodeLift(frame, relative, surfaceTexture.Load(pixel, 0u).w);
    float3 world = relative + normal * lift + frame.cameraPosition.xyz;
    float3 origin = world + normal * GetRayOffset(frame, relative);
    RaytracingAccelerationStructure scene = GetTracedAcceleration();

    // The sun, through a random point of its disc, lit as often as its light gets through, its colour over its
    // brightness the tint.
    float3 toSun = lights.sunDirection.xyz;
    float3 xi = GetRandom(pixel, frame.frameIndex, 0u);
    float3 sunRay = SampleSunDirection(toSun, xi.xy);
    float3 sunThrough = dot(normal, toSun) > 0.0 ? GetTransmittance(scene, origin, sunRay, 1e6) : float3(0.0);
    float sunBrightness = GetLuminance(sunThrough);
    if (sunBrightness > xi.z)
        shadows.words = EncodeShadow(shadows.words, 0u, 1.0);

    if (sunBrightness > 0.0)
        shadows.sunTint = sunThrough / sunBrightness;

    // The tile's lights in the order shading reads them, until the slots run out.
    ArrayRef<uint> tiles = { lights.tiles };
    uint tile = GetLightTileOffset(pixel / LightTileSize, frame.width);
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
            LightRay ray = CreateLightRay(origin, target);
            if (dot(normal, ray.direction) > 0.0)
            {
                float through = GetLuminance(GetTransmittance(scene, origin, ray.direction, ray.distance));
                shadows.words = EncodeShadow(shadows.words, slot, through);
            }

            slot++;
        }
    }

    return shadows;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    Lights lights = GetLights();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(denoise.width, denoise.height);
    if (local.x == 0u && local.y < 2u)
        groupSunTiles[local.y] = 0u;

    GroupMemoryBarrierWithGroupSync();
    bool isInside = all(thread.xy < size);
    if (isInside)
    {
        PixelShadows shadows = Trace(frame, lights, GetPicturePixel(frame, thread.xy, size, float2(0.0)));
        StoreShadowMask(denoise.mask, thread.xy, size.x, shadows.words);
        if (denoise.sunTint != 0u)
        {
            StorageRef tints = { denoise.sunTint, 0u };
            tints.Store(thread.xy, float4(shadows.sunTint, 1.0));
        }

        if (GetShadow(shadows.words, 0u) > 0.5)
            InterlockedOr(groupSunTiles[local.y / SunTileSize.y], GetSunTileBit(thread.xy));
    }

    GroupMemoryBarrierWithGroupSync();
    if (isInside && local.x == 0u && local.y % SunTileSize.y == 0u)
    {
        ArrayRef<uint> sunTiles = { denoise.sunTiles };
        sunTiles[GetSunTileIndex(thread.xy / SunTileSize, size.x)] = groupSunTiles[local.y / SunTileSize.y];
    }
}
