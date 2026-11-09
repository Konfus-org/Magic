// The light each smooth surface reflects, traced at half the picture's size: from one pixel of each 2 by 2, a ray along
// a direction its specular reflects the view into, drawn by its roughness, bringing back the sky where it hits nothing
// and the light of the surface it lands on, lit by one light at random and by the probes; then resolved over a few
// neighbours, each weighed by how much the pixel's own specular would reflect its ray, and blended with the frames
// before where the surface, or what it reflects, was. Shading reads the result through Scene/TracedReflections.
// Adapted from Wicked Engine's ray-traced reflections and its stochastic screen-space reflection resolve. The pass
// header's x is the frame's buffer, which holds the lights, y the probe grid's constants and w the traced reflection
// constants.
module "TraceReflections";

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
import Scene.TracedReflections;
import Sky.ISky;
import Surface.Brdf;
import Trace.HitSurface;
import Trace.HitLight;
import Scene.Models;

// How far a ray reaches, and the distance a ray that hits nothing counts as having gone.
static const float RayRange = 10000.0;

// How far in traced pixels the resolve reaches, from the smoothest surface to one a fifth rough, and how many
// neighbours it reads there.
static const float ResolveSpreadSmooth = 2.0;
static const float ResolveSpreadRough = 8.0;
static const uint ResolveSamples = 4u;

// How much of the history a pixel keeps a frame, how many of its neighbours' deviations the history is held within,
// and how much of the variance's history it keeps.
static const float HistoryBlend = 0.95;
static const float HistorySpread = 2.0;
static const float VarianceBlend = 0.9;

// The surface at a picture's pixel, where something was drawn there: its camera-relative point, unit normal, roughness
// and the unit direction to the viewer.
struct ReflectingSurface
{
    float3 relative;
    float3 normal;
    float roughness;
    float3 view;
}

bool LoadReflectingSurface(FrameConstants frame, uint2 pixel, out ReflectingSurface surface)
{
    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float depth = depthTexture.Load(pixel, 0u).x;
    surface.roughness = surfaceTexture.Load(pixel, 0u).x;
    surface.normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    surface.relative = ReconstructRelative(frame, GetTexelCenterUv(pixel, uint2(frame.width, frame.height)), depth);
    surface.view = NormalizeSafe(-surface.relative, surface.normal);
    return depth > 0.0 && surface.roughness <= TracedRoughness;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceReflections<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    TracedReflectionConstants reflections = GetTracedReflections();
    if (any(thread.xy >= GetTracedSize(frame)))
        return;

    StorageRef traced = { reflections.traced, 0u };
    StorageRef rays = { reflections.rays, 0u };
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, GetTracedPixel(frame, thread.xy), surface))
    {
        traced.Store(thread.xy, float4(0.0, 0.0, 0.0, RayRange));
        rays.Store(thread.xy, float4(0.0));
        return;
    }

    float4 ray = SampleGgxReflection(
        surface.normal, surface.view, surface.roughness, GetRandom(thread.xy, frame.frameIndex, 1u).xy);
    float3 world = surface.relative + frame.cameraPosition.xyz;
    float3 origin = world + surface.normal * GetRayOffset(frame, surface.relative);
    AccelerationRef acceleration = { GetModels().acceleration };
    RaytracingAccelerationStructure scene = acceleration.Get();
    RayHit hit = GetClosestHit(scene, origin, ray.xyz, RayRange, RAY_FLAG_CULL_BACK_FACING_TRIANGLES);
    Lights lights = GetLights();
    float4 light = float4(GetSkyRadiance<S>(lights, ray.xyz), RayRange);
    if (hit.isHit)
    {
        HitSurface landed = LoadHitSurface(hit, origin, ray.xyz);
        float3 xi = GetRandom(thread.xy, frame.frameIndex, 2u);
        light = float4(GetHitLight<S>(lights, GetProbeGrid(), scene, landed, xi), hit.distance);
    }

    traced.Store(thread.xy, light);
    rays.Store(thread.xy, ray);
}

// How much a surface's specular reflects towards its viewer of a neighbour's ray, over the density it was drawn at, so
// a ray drawn often counts for no more than its share.
float GetRayWeight(ReflectingSurface surface, float4 ray)
{
    float3 half = normalize(ray.xyz + surface.view);
    float nDotL = saturate(dot(surface.normal, ray.xyz));
    float nDotV = saturate(dot(surface.normal, surface.view));
    float alpha = Pow2(max(surface.roughness, 0.045));
    float specular = VisibilitySmith(alpha, nDotV, nDotL) * DistributionGgx(alpha, saturate(dot(surface.normal, half)));
    return specular * nDotL / max(ray.w, 1e-5);
}

// A thread a traced pixel: the light of a few neighbours' rays spread at random around it, farther as the surface
// roughens, each weighed by GetRayWeight, the variance of their brightness, and the farthest of their rays that count.
// The light is averaged in a curve that keeps a bright neighbour from outweighing the rest.
[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveReflections(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    TracedReflectionConstants reflections = GetTracedReflections();
    uint2 size = GetTracedSize(frame);
    if (any(thread.xy >= size))
        return;

    TextureRef traced = { reflections.traced };
    TextureRef rays = { reflections.rays };
    StorageRef resolved = { reflections.resolved, 0u };
    StorageRef distance = { reflections.distance, 0u };
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, thread.xy * TracedScale, surface))
    {
        resolved.Store(thread.xy, float4(0.0));
        distance.Store(thread.xy, float4(RayRange, 0.0, 0.0, 0.0));
        return;
    }

    // A Hammersley set shifted at random per pixel and frame, its brightness's variance taken as it goes (West,
    // "Updating Mean and Variance Estimates: An Improved Method", 1979).
    float spread = lerp(ResolveSpreadSmooth, ResolveSpreadRough, saturate(surface.roughness * 5.0));
    float2 shift = GetRandom(thread.xy, frame.frameIndex, 0u).xy;
    float3 sum = float3(0.0);
    float weightSum = 0.0;
    float mean = 0.0;
    float squares = 0.0;
    float farthest = 0.0;
    for (uint i = 0u; i < ResolveSamples; i++)
    {
        float2 offset = (frac(GetHammersley(i, ResolveSamples) + shift) - 0.5) * spread;
        uint2 neighbour = uint2(int2(float2(thread.xy) + offset));
        if (any(neighbour >= size))
            continue;

        float4 ray = rays.Load(neighbour, 0u);
        float weight = ray.w > 0.0 ? GetRayWeight(surface, ray) : 0.0;
        if (weight <= 0.0)
            continue;

        float4 light = traced.Load(neighbour, 0u);
        float3 color = light.rgb / (1.0 + GetLuminance(light.rgb));
        sum += color * weight;
        weightSum += weight;
        float luminance = GetLuminance(color);
        float lastMean = mean;
        mean += weight / weightSum * (luminance - lastMean);
        squares += weight * (luminance - lastMean) * (luminance - mean);
        if (weight > 0.001)
            farthest = max(farthest, light.a);
    }

    float4 own = traced.Load(thread.xy, 0u);
    float4 result = float4(own.rgb, 0.0);
    if (weightSum > 0.0)
    {
        float3 average = sum / weightSum;
        result = float4(average / max(1.0 - GetLuminance(average), Epsilon), squares / weightSum);
    }

    resolved.Store(thread.xy, float4(max(result.rgb, 1e-5), result.a));
    distance.Store(thread.xy, float4(weightSum > 0.0 ? farthest : own.a, 0.0, 0.0, 0.0));
}

// Where on last frame's screen the reflection at a surface was: the point the ray's length puts behind it along the
// view, as a mirror shows it, seen from last frame's camera.
float2 GetReflectedHistoryUv(FrameConstants frame, ReflectingSurface surface, float rayLength)
{
    float3 reflected = surface.relative - surface.view * rayLength;
    float3 previous = reflected + frame.cameraPosition.xyz - frame.previousCameraPosition.xyz;
    float4 clip = mul(float4(previous, 1.0), frame.previousViewProjection);
    return ToUv(clip.xy / max(clip.w, Epsilon));
}

// A thread a traced pixel: its resolved light blended with the history where the surface was last frame, or where what
// it reflects was, whichever is nearer its neighbours' mean (Stachowiak, "Stochastic all the things", 2018).
[shader("compute")]
[numthreads(8, 8, 1)]
void BlendReflections(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    TracedReflectionConstants reflections = GetTracedReflections();
    uint2 size = GetTracedSize(frame);
    if (any(thread.xy >= size))
        return;

    uint2 pixel = thread.xy * TracedScale;
    TextureRef resolved = { reflections.resolved };
    StorageRef light = { reflections.light, 0u };
    StorageRef depthTarget = { reflections.depth, 0u };
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    float4 current = resolved.Load(thread.xy, 0u);
    depthTarget.Store(thread.xy, float4(depth > 0.0 ? ToViewDepth(depth, frame.near) : 0.0, 0.0, 0.0, 0.0));
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, pixel, surface) || reflections.hasHistory == 0u)
    {
        light.Store(thread.xy, current);
        return;
    }

    float3 mean;
    float3 deviation;
    GetNeighbourhood(resolved, thread.xy, size, mean, deviation);

    TracedHistory history;
    history.light.id = reflections.lightHistory;
    history.depth.id = reflections.depthHistory;
    history.sampler.id = reflections.sampler;
    history.size = size;
    TextureRef velocityTexture = { frame.velocity };
    TextureRef distance = { reflections.distance };
    float3 velocity = velocityTexture.Load(pixel, 0u).xyz;
    float2 surfaceUv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    float2 reflectedUv = GetReflectedHistoryUv(frame, surface, distance.Load(thread.xy, 0u).x);
    float surfaceGap = abs(GetLuminance(history.light.SampleLevel(history.sampler, surfaceUv, 0.0).rgb) - GetLuminance(mean));
    float reflectedGap = abs(GetLuminance(history.light.SampleLevel(history.sampler, reflectedUv, 0.0).rgb) - GetLuminance(mean));
    float2 uv = surfaceGap < reflectedGap ? surfaceUv : reflectedUv;
    float sameness;
    float4 previous = SampleHistory(history, velocity.z, uv, sameness);
    light.Store(
        thread.xy, BlendHistory(current, previous, sameness, uv, mean, deviation, HistoryBlend, HistorySpread, VarianceBlend));
}
