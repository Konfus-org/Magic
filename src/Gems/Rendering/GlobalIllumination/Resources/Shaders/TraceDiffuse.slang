// The light that reaches each surface indirectly, traced at a part of the picture's size: from one pixel of each traced
// pixel's footprint, a few rays over its hemisphere as the diffuse lobe weighs it, each bringing back the light of the
// surface it lands on, lit by one light at random and by the probes, or the probes' light at the pixel where it reaches
// nothing within range, then filtered over its neighbours, and blended with the frames before where they saw the same
// surface. Shading reads the result through Light/Indirect. Adapted from Wicked Engine's ray-traced diffuse. The pass
// header's x is the frame's buffer, which holds the lights and the indirect light. Its y is the GI buffer.
module "TraceDiffuse";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Light.Probes;
import Light.Indirect;
import Traced;
import Sky.ISky;
import Trace.HitSurface;
import Scene.Models;

// How far in traced pixels the filter reaches, and how many neighbours it reads there.
static const float FilterSpread = 8.0;
static const uint FilterSamples = 4u;

// How many of its neighbours' deviations a pixel's history is held within, and how much of the variance's history it
// keeps.
static const float HistorySpread = 0.9;
static const float VarianceBlend = 0.6;

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceDiffuse<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GIConstants gi = GetGIConstants();
    TracedDiffuseConstants diffuse = gi.diffuse;
    if (any(thread.xy >= gi.tracedSize))
        return;

    uint2 pixel = GetTracedPixel(frame, thread.xy, gi.tracedSize);
    StorageRef traced = { diffuse.traced, 0u };
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0)
    {
        traced.Store(thread.xy, float4(0.0));
        return;
    }

    Lights lights = GetLights();
    ProbeGridConstants grid = GetProbeGrid();
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    float3 relative = ReconstructPixel(frame, pixel, depth);
    float3 world = relative + frame.cameraPosition.xyz;
    float3 origin = world + normal * GetRayOffset(frame, relative);
    float3 beyond = GetProbeIrradiance(grid, world, normal);
    float3x3 basis = CreateBasis(normal);
    RaytracingAccelerationStructure scene = GetTracedAcceleration();
    float3 sum = float3(0.0);
    for (uint i = 0u; i < gi.diffuseRayCount; i++)
    {
        float3 direction = ToWorld(basis, SampleCosineHemisphere(GetRandom(thread.xy, frame.frameIndex, 1u + i).xy));
        RayHit hit = GetClosestHit(scene, origin, direction, gi.diffuseRayRange, RAY_FLAG_CULL_BACK_FACING_TRIANGLES);
        if (!hit.isHit)
        {
            sum += beyond;
            continue;
        }

        HitSurface surface = LoadHitSurface(hit, origin, direction);
        float3 xi = GetRandom(thread.xy, frame.frameIndex, 1u + gi.diffuseRayCount + i);
        sum += GetHitLight<S>(lights, grid, scene, surface, xi);
    }

    traced.Store(thread.xy, float4(sum / float(gi.diffuseRayCount), 1.0));
}

// A thread a traced pixel: its light filtered over a few neighbours spread at random through the 8 by 8 traced pixels
// around it, weighed by how near their depths are, as Traced's NeighbourLight averages them.
[shader("compute")]
[numthreads(8, 8, 1)]
void FilterDiffuse(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GIConstants gi = GetGIConstants();
    TracedDiffuseConstants diffuse = gi.diffuse;
    uint2 size = gi.tracedSize;
    if (any(thread.xy >= size))
        return;

    TextureRef depthTexture = { frame.depth };
    TextureRef traced = { diffuse.traced };
    StorageRef filtered = { diffuse.filtered, 0u };
    float depth = depthTexture.Load(GetPicturePixel(frame, thread.xy, size, float2(0.0)), 0u).x;
    if (depth == 0.0)
    {
        filtered.Store(thread.xy, float4(0.0));
        return;
    }

    float viewDepth = ToViewDepth(depth, frame.near);
    float2 shift = GetRandom(thread.xy, frame.frameIndex, 0u).xy;
    NeighbourLight neighbours = {};
    for (uint i = 0u; i < FilterSamples; i++)
    {
        uint2 neighbour = GetNeighbour(thread.xy, i, FilterSamples, shift, FilterSpread);
        if (any(neighbour >= size))
            continue;

        // No weight at a tenth of the depth apart, relative so a surface seen at a glancing angle keeps its neighbours.
        float neighbourDepth = depthTexture.Load(GetPicturePixel(frame, neighbour, size, float2(0.0)), 0u).x;
        float gap = abs(viewDepth - ToViewDepth(neighbourDepth, frame.near)) / viewDepth;
        float weight = neighbourDepth > 0.0 ? 1.0 - saturate(gap * 10.0) : 0.0;
        if (weight <= 0.0)
            continue;

        neighbours = AddNeighbour(neighbours, traced.Load(neighbour, 0u).rgb, weight);
    }

    filtered.Store(thread.xy, GetNeighbourAverage(neighbours, traced.Load(thread.xy, 0u).rgb));
}

// A thread a traced pixel: its filtered light blended with the history where the surface was last frame, as
// Traced's BlendHistory blends it.
[shader("compute")]
[numthreads(8, 8, 1)]
void BlendDiffuse(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GIConstants gi = GetGIConstants();
    uint2 size = gi.tracedSize;
    if (any(thread.xy >= size))
        return;

    uint2 pixel = GetPicturePixel(frame, thread.xy, size, float2(0.0));
    TracedBlendConstants blend = gi.diffuse.blend;
    TextureRef depthTexture = { frame.depth };
    TextureRef filtered = { gi.diffuse.filtered };
    StorageRef light = { blend.light, 0u };
    float depth = depthTexture.Load(pixel, 0u).x;
    float4 current = filtered.Load(thread.xy, 0u);

    // The view depth kept for the next frame's history of both blends: 0 where nothing was drawn.
    StorageRef viewDepth = { gi.depth, 0u };
    viewDepth.Store(thread.xy, float4(depth > 0.0 ? ToViewDepth(depth, frame.near) : 0.0, 0.0, 0.0, 0.0));
    if (blend.hasHistory == 0u || depth == 0.0)
    {
        light.Store(thread.xy, current);
        return;
    }

    Neighbourhood neighbourhood = GetNeighbourhood(filtered, thread.xy, size);
    TextureRef velocityTexture = { frame.velocity };
    float3 velocity = velocityTexture.Load(pixel, 0u).xyz;
    float2 uv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    light.Store(
        thread.xy,
        BlendHistory(gi, blend, current, neighbourhood, velocity.z, uv, gi.diffuseHistory, HistorySpread, VarianceBlend));
}
