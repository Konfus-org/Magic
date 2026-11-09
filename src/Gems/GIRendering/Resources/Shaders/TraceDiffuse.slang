// The light that reaches each surface indirectly, traced at half the picture's size: from one pixel of each 2 by 2, two
// rays over its hemisphere as the diffuse lobe weighs it, each bringing back the light of the surface it lands on, lit by
// one light at random and by the probes, or the probes' light at the pixel where it reaches nothing within range; then
// filtered over its neighbours, and blended with the frames before where they saw the same surface. Shading reads the
// result through Scene/TracedDiffuse. Adapted from Wicked Engine's ray-traced diffuse. The pass header's x is the
// frame's buffer, which holds the lights, y the probe grid's constants and w the traced diffuse constants.
module "TraceDiffuse";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Color;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Scene.Probes;
import Scene.Traced;
import Scene.TracedDiffuse;
import Sky.ISky;
import Trace.HitSurface;
import Trace.HitLight;
import Scene.Models;

// The rays a traced pixel sends, and how far each reaches before the probes' light stands for what lies beyond.
static const uint RayCount = 2u;
static const float RayRange = 10.0;

// How far in traced pixels the filter reaches, and how many neighbours it reads there.
static const float FilterSpread = 8.0;
static const uint FilterSamples = 4u;

// How much of the history a pixel keeps a frame, how many of its neighbours' deviations the history is held within,
// and how much of the variance's history it keeps.
static const float HistoryBlend = 0.98;
static const float HistorySpread = 0.9;
static const float VarianceBlend = 0.6;

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceDiffuse<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    TracedDiffuseConstants diffuse = GetTracedDiffuse();
    if (any(thread.xy >= GetTracedSize(frame)))
        return;

    uint2 pixel = GetTracedPixel(frame, thread.xy);
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
    TextureRef normalTexture = { frame.gbufferNormal };
    float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    float3 relative = ReconstructRelative(frame, GetTexelCenterUv(pixel, uint2(frame.width, frame.height)), depth);
    float3 world = relative + frame.cameraPosition.xyz;
    float3 origin = world + normal * GetRayOffset(frame, relative);
    float3 beyond = GetProbeIrradiance(grid, world, normal);
    float3x3 basis = CreateBasis(normal);
    AccelerationRef acceleration = { GetModels().acceleration };
    RaytracingAccelerationStructure scene = acceleration.Get();
    float3 sum = float3(0.0);
    for (uint i = 0u; i < RayCount; i++)
    {
        float pdf;
        float3 direction = ToWorld(basis, SampleCosineHemisphere(GetRandom(thread.xy, frame.frameIndex, 1u + i).xy, pdf));
        RayHit hit = GetClosestHit(scene, origin, direction, RayRange, RAY_FLAG_CULL_BACK_FACING_TRIANGLES);
        if (!hit.isHit)
        {
            sum += beyond;
            continue;
        }

        HitSurface surface = LoadHitSurface(hit, origin, direction);
        float3 xi = GetRandom(thread.xy, frame.frameIndex, 1u + RayCount + i);
        sum += GetHitLight<S>(lights, grid, scene, surface, xi);
    }

    traced.Store(thread.xy, float4(sum / float(RayCount), 1.0));
}

// A thread a traced pixel: its light filtered over a few neighbours spread at random through the 8 by 8 traced pixels
// around it, weighed by how near their depths are, and the variance of their brightness. The light is averaged in a
// curve that keeps a bright neighbour from outweighing the rest.
[shader("compute")]
[numthreads(8, 8, 1)]
void FilterDiffuse(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    TracedDiffuseConstants diffuse = GetTracedDiffuse();
    uint2 size = GetTracedSize(frame);
    if (any(thread.xy >= size))
        return;

    TextureRef depthTexture = { frame.depth };
    TextureRef traced = { diffuse.traced };
    StorageRef filtered = { diffuse.filtered, 0u };
    float depth = depthTexture.Load(thread.xy * TracedScale, 0u).x;
    if (depth == 0.0)
    {
        filtered.Store(thread.xy, float4(0.0));
        return;
    }

    // A Hammersley set shifted at random per pixel and frame, its brightness's variance taken as it goes (West,
    // "Updating Mean and Variance Estimates: An Improved Method", 1979).
    float viewDepth = ToViewDepth(depth, frame.near);
    float2 shift = GetRandom(thread.xy, frame.frameIndex, 0u).xy;
    float3 sum = float3(0.0);
    float weightSum = 0.0;
    float mean = 0.0;
    float squares = 0.0;
    for (uint i = 0u; i < FilterSamples; i++)
    {
        float2 offset = (frac(GetHammersley(i, FilterSamples) + shift) - 0.5) * FilterSpread;
        uint2 neighbour = uint2(int2(float2(thread.xy) + offset));
        if (any(neighbour >= size))
            continue;

        // No weight at a tenth of the depth apart, relative so a surface seen at a glancing angle keeps its neighbours.
        float neighbourDepth = depthTexture.Load(neighbour * TracedScale, 0u).x;
        float gap = abs(viewDepth - ToViewDepth(neighbourDepth, frame.near)) / viewDepth;
        float weight = neighbourDepth > 0.0 ? 1.0 - saturate(gap * 10.0) : 0.0;
        if (weight <= 0.0)
            continue;

        float3 color = traced.Load(neighbour, 0u).rgb;
        color /= 1.0 + GetLuminance(color);
        sum += color * weight;
        weightSum += weight;
        float luminance = GetLuminance(color);
        float lastMean = mean;
        mean += weight / weightSum * (luminance - lastMean);
        squares += weight * (luminance - lastMean) * (luminance - mean);
    }

    float4 result = float4(traced.Load(thread.xy, 0u).rgb, 0.0);
    if (weightSum > 0.0)
    {
        float3 average = sum / weightSum;
        result = float4(average / max(1.0 - GetLuminance(average), Epsilon), squares / weightSum);
    }

    filtered.Store(thread.xy, float4(max(result.rgb, 1e-5), result.a));
}

// A thread a traced pixel: its filtered light blended with the history where the surface was last frame, as
// Scene/Traced's BlendHistory blends it.
[shader("compute")]
[numthreads(8, 8, 1)]
void BlendDiffuse(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    TracedDiffuseConstants diffuse = GetTracedDiffuse();
    uint2 size = GetTracedSize(frame);
    if (any(thread.xy >= size))
        return;

    uint2 pixel = thread.xy * TracedScale;
    TextureRef depthTexture = { frame.depth };
    TextureRef filtered = { diffuse.filtered };
    StorageRef light = { diffuse.light, 0u };
    StorageRef depthTarget = { diffuse.depth, 0u };
    float depth = depthTexture.Load(pixel, 0u).x;
    float4 current = filtered.Load(thread.xy, 0u);
    depthTarget.Store(thread.xy, float4(depth > 0.0 ? ToViewDepth(depth, frame.near) : 0.0, 0.0, 0.0, 0.0));
    // The history holds nothing of this world while the probes hold nothing of it either, as after a domain.
    if (diffuse.hasHistory == 0u || GetProbeGrid().hasHistory == 0u || depth == 0.0)
    {
        light.Store(thread.xy, current);
        return;
    }

    float3 mean;
    float3 deviation;
    GetNeighbourhood(filtered, thread.xy, size, mean, deviation);

    TracedHistory history;
    history.light.id = diffuse.lightHistory;
    history.depth.id = diffuse.depthHistory;
    history.sampler.id = diffuse.sampler;
    history.size = size;
    TextureRef velocityTexture = { frame.velocity };
    float3 velocity = velocityTexture.Load(pixel, 0u).xyz;
    float2 uv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    float sameness;
    float4 previous = SampleHistory(history, velocity.z, uv, sameness);
    light.Store(
        thread.xy, BlendHistory(current, previous, sameness, uv, mean, deviation, HistoryBlend, HistorySpread, VarianceBlend));
}
