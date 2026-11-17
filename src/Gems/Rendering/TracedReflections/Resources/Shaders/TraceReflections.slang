// The light each smooth surface reflects, traced at a part of the picture's size: from one pixel of each traced pixel's
// footprint, a ray along a direction its specular reflects the view into, drawn by its roughness, bringing back the sky
// where it hits nothing and the light of the surface it lands on, lit by the sun, one light at random and the probes,
// and its specular as a rough mirror of the probes. Then resolved over a few neighbours on the same surface, each held
// to the brightness around it and weighed by how much the pixel's own specular would reflect its ray, and
// blended with the frames before where the surface, or what it reflects, was. Shading reads the result through
// Light/Indirect.
// Adapted from Wicked Engine's ray-traced reflections and its stochastic screen-space reflection resolve. The pass
// header's x is the frame's buffer, which holds the lights and the indirect light. Its y is the reflections' buffer.
module "TraceReflections";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Random;
import Core.Pack;
import Scene.Lights;
import Scene.Instances;
import Light.Probes;
import Light.Indirect;
import Traced;
import Sky.ISky;
import Surface.Brdf;
import Trace.HitSurface;
import Scene.Models;

// How far in traced pixels the resolve reaches, from the smoothest surface to one a fifth rough, and how many
// neighbours it reads there.
static const float ResolveSpreadSmooth = 2.0;
static const float ResolveSpreadRough = 8.0;
static const uint ResolveSamples = 4u;

// How fast a neighbour's weight in the resolve falls with the distance between its plane and the pixel's, as a part of
// the pixel's distance, and with the difference of their roughness.
static const float ResolvePlaneSharpness = 10000.0;
static const float ResolveRoughnessSharpness = 10.0;

// How many deviations over its neighbours' mean brightness a ray's light is held to.
static const float FireflySpread = 2.0;

// The most a traced texture, of half floats, holds: the limit where too few neighbours set one.
static const float NoLimit = 65504.0;

// How many of its neighbours' deviations a pixel's history is held within, and how much of the variance's history it
// keeps.
static const float HistorySpread = 2.0;
static const float VarianceBlend = 0.9;

public struct TracedReflectionConstants
{
    // The rays' light at each traced pixel, with how far each went in alpha, and its direction with the density it was
    // drawn at.
    public uint traced;
    public uint rays;
    // The rays' light resolved over the neighbours, with its brightness's variance in alpha, and the farthest of their
    // rays that count.
    public uint resolved;
    public uint distance;
    public TracedBlendConstants blend;
}

// The reflections' buffer, C#'s ReflectionConstants: their textures, the indirect light's view depth of last frame,
// which the blend reads, what it reads its history with, the traced textures' size, how far the rays reach and how
// much of its history the blend keeps a frame.
public struct ReflectionConstants
{
    public TracedReflectionConstants reflections;
    public uint depthHistory;
    public uint sampler;
    public uint2 tracedSize;
    public float reflectionRayRange;
    public float reflectionHistory;
}

ReflectionConstants GetReflectionConstants() { return LoadConstants<ReflectionConstants>(GetPassHeader().y); }

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
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float depth = depthTexture.Load(pixel, 0u).x;
    surface.roughness = surfaceTexture.Load(pixel, 0u).x;
    surface.normal = LoadNormal(frame.gbufferNormal, pixel);
    surface.relative = ReconstructPixel(frame, pixel, depth);
    surface.view = NormalizeSafe(-surface.relative, surface.normal);
    return depth > 0.0 && surface.roughness <= TracedRoughness;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceReflections<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ReflectionConstants constants = GetReflectionConstants();
    TracedReflectionConstants reflections = constants.reflections;
    if (any(thread.xy >= constants.tracedSize))
        return;

    StorageRef traced = { reflections.traced, 0u };
    StorageRef rays = { reflections.rays, 0u };
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, GetTracedPixel(frame, thread.xy, constants.tracedSize), surface))
    {
        traced.Store(thread.xy, float4(0.0, 0.0, 0.0, constants.reflectionRayRange));
        rays.Store(thread.xy, float4(0.0));
        return;
    }

    float4 ray = SampleGgxReflection(
        surface.normal, surface.view, surface.roughness, GetRandom(thread.xy, frame.frameIndex, 1u).xy);

    // A ray drawn below the surface, as a surface seen edge-on draws often, is mirrored above it: else it lands on
    // the back of its own mesh and reflects black.
    ray.xyz -= 2.0 * min(dot(ray.xyz, surface.normal), 0.0) * surface.normal;
    float3 world = surface.relative + frame.cameraPosition.xyz;
    float3 origin = world + surface.normal * GetRayOffset(frame, surface.relative);
    RaytracingAccelerationStructure scene = GetTracedAcceleration();
    // A ray that lands on a back face is inside a mesh, as a ray from the seam between two meshes is: it reflects
    // black, not the sky beyond the wall.
    RayHit hit = GetClosestHit(scene, origin, ray.xyz, constants.reflectionRayRange, RAY_FLAG_NONE);
    Lights lights = GetLights();
    float4 light = float4(GetSkyRadiance<S>(lights, ray.xyz) * hit.through, constants.reflectionRayRange);
    if (hit.isHit && !hit.isFrontFace)
        light = float4(0.0, 0.0, 0.0, hit.distance);
    else if (hit.isHit)
    {
        HitSurface landed = LoadHitSurface(hit, origin, ray.xyz);
        float3 xi = GetRandom(thread.xy, frame.frameIndex, 2u);
        ProbeGridConstants grid = GetProbeGrid();
        float3 hitLight = GetHitLight<S>(lights, grid, scene, landed, xi) + GetHitSpecular(grid, hit, landed, ray.xyz);
        light = float4(hitLight * hit.through, hit.distance);
    }

    traced.Store(thread.xy, light);
    rays.Store(thread.xy, ray);
}

// PROTO: the light the specular of a surface a ray landed on sends back along it, as a rough mirror: its F0, which the
// hit materials row keeps in its free word, times the probes' light along the ray's reflection off it. So a metal,
// whose diffuse is black, reflects its surroundings.
float3 GetHitSpecular(ProbeGridConstants grid, RayHit hit, HitSurface landed, float3 direction)
{
    Models models = GetModels();
    GpuInstance instance = LoadInstance(models.instances, hit.instance);
    uint slot = LoadMesh(models.meshes, hit.mesh).materialSlot;
    ArrayRef<GpuHitMaterial> materials = { GetTracedWorld().hitMaterials };
    float3 f0 = UnpackUnorm4x8(materials[GetMaterial(instance, slot)].padding1).rgb;
    return f0 * GetProbeIrradiance(grid, landed.position, reflect(direction, landed.normal));
}

// The brightness a resolve holds its rays' light to around a traced pixel: the mean of its 3 by 3 neighbours' traced
// brightness plus FireflySpread deviations, with the brightest left out, so one bright ray does not raise its own
// limit. A neighbour that traced nothing, or brought back black, counts for none.
float GetFireflyLimit(TextureRef traced, uint2 center, uint2 size)
{
    float sum = 0.0;
    float squares = 0.0;
    float brightest = 0.0;
    float count = 0.0;
    for (uint i = 0u; i < 9u; i++)
    {
        uint2 neighbour = uint2(clamp(int2(center) + int2(i % 3u, i / 3u) - 1, int2(0), int2(size) - 1));
        float luminance = GetLuminance(traced.Load(neighbour, 0u).rgb);
        if (luminance <= 0.0)
            continue;

        sum += luminance;
        squares += luminance * luminance;
        brightest = max(brightest, luminance);
        count += 1.0;
    }

    if (count < 3.0)
        return NoLimit;

    float mean = (sum - brightest) / (count - 1.0);
    float variance = (squares - brightest * brightest) / (count - 1.0) - mean * mean;
    return mean + FireflySpread * sqrt(max(variance, 0.0));
}

// A ray's light, as bright as a limit at most.
float3 GetHeldLight(float3 light, float limit)
{
    float luminance = GetLuminance(light);
    return luminance > limit ? light * (limit / luminance) : light;
}

// How much a neighbour's surface is the pixel's own: 1 on its plane, facing its way and as rough, less as they part.
float GetSurfaceSameness(ReflectingSurface surface, ReflectingSurface neighbour)
{
    float planeError = abs(dot(neighbour.relative - surface.relative, surface.normal)) / length(surface.relative);
    return exp(-Pow2(planeError) * ResolvePlaneSharpness) * Pow4(saturate(dot(surface.normal, neighbour.normal)))
        * exp(-abs(surface.roughness - neighbour.roughness) * ResolveRoughnessSharpness);
}

// How much a surface's specular reflects towards its viewer of a neighbour's ray, over the density it was drawn at, so
// a ray drawn often counts for no more than its share.
float GetRayWeight(ReflectingSurface surface, float4 ray)
{
    float3 half = normalize(ray.xyz + surface.view);
    float nDotL = saturate(dot(surface.normal, ray.xyz));
    float nDotV = saturate(dot(surface.normal, surface.view));
    float alpha = GetReflectionAlpha(surface.roughness);
    float specular = VisibilitySmith(alpha, nDotV, nDotL) * DistributionGgx(alpha, saturate(dot(surface.normal, half)));
    return specular * nDotL / max(ray.w, 1e-5);
}

// A thread a traced pixel: the light of its own ray and a few neighbours' spread at random around it, farther as the
// surface roughens, each weighed by GetRayWeight times how much its surface is the pixel's own, as Traced's
// NeighbourLight averages them in a curve that bends at that limit, each held to it, and the farthest of their rays that count. Where a corner
// turns the neighbours' rays away, its own ray still averages with any that remain.
[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveReflections(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ReflectionConstants constants = GetReflectionConstants();
    TracedReflectionConstants reflections = constants.reflections;
    uint2 size = constants.tracedSize;
    if (any(thread.xy >= size))
        return;

    TextureRef traced = { reflections.traced };
    TextureRef rays = { reflections.rays };
    StorageRef resolved = { reflections.resolved, 0u };
    StorageRef distance = { reflections.distance, 0u };
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, GetPicturePixel(frame, thread.xy, size, float2(0.0)), surface))
    {
        resolved.Store(thread.xy, float4(0.0));
        distance.Store(thread.xy, float4(constants.reflectionRayRange, 0.0, 0.0, 0.0));
        return;
    }

    float spread = lerp(ResolveSpreadSmooth, ResolveSpreadRough, saturate(surface.roughness * 5.0));
    float2 shift = GetRandom(thread.xy, frame.frameIndex, 0u).xy;
    float limit = GetFireflyLimit(traced, thread.xy, size);
    NeighbourLight neighbours = CreateNeighbourLight(limit);
    float farthest = 0.0;
    for (uint i = 0u; i <= ResolveSamples; i++)
    {
        uint2 neighbour = i < ResolveSamples ? GetNeighbour(thread.xy, i, ResolveSamples, shift, spread) : thread.xy;
        if (any(neighbour >= size))
            continue;

        float4 ray = rays.Load(neighbour, 0u);
        float weight = ray.w > 0.0 ? GetRayWeight(surface, ray) : 0.0;
        if (i < ResolveSamples && weight > 0.0)
        {
            ReflectingSurface neighbourSurface;
            uint2 pixel = GetPicturePixel(frame, neighbour, size, float2(0.0));
            bool isTraced = LoadReflectingSurface(frame, pixel, neighbourSurface);
            weight = isTraced ? weight * GetSurfaceSameness(surface, neighbourSurface) : 0.0;
        }

        if (weight <= 0.0)
            continue;

        float4 light = traced.Load(neighbour, 0u);
        neighbours = AddNeighbour(neighbours, GetHeldLight(light.rgb, limit), weight);
        if (weight > 0.001)
            farthest = max(farthest, light.a);
    }

    float4 own = traced.Load(thread.xy, 0u);
    resolved.Store(thread.xy, GetNeighbourAverage(neighbours, GetHeldLight(own.rgb, limit)));
    distance.Store(thread.xy, float4(neighbours.weightSum > 0.0 ? farthest : own.a, 0.0, 0.0, 0.0));
}

// Where on last frame's screen the reflection at a surface was: the point the ray's length puts behind it along the
// view, as a mirror shows it, seen from last frame's camera.
float2 GetReflectedHistoryUv(FrameConstants frame, ReflectingSurface surface, float rayLength)
{
    float3 reflected = surface.relative - surface.view * rayLength;
    float3 previous = reflected + frame.cameraPosition.xyz - frame.previousCameraPosition.xyz;
    return GetPreviousScreen(frame, previous).xy;
}

// A thread a traced pixel: its resolved light blended with the history where the surface was last frame, or where what
// it reflects was, whichever is nearer its neighbours' mean (Stachowiak, "Stochastic all the things", 2018).
[shader("compute")]
[numthreads(8, 8, 1)]
void BlendReflections(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ReflectionConstants constants = GetReflectionConstants();
    uint2 size = constants.tracedSize;
    if (any(thread.xy >= size))
        return;

    uint2 pixel = GetPicturePixel(frame, thread.xy, size, float2(0.0));
    TracedBlendConstants blend = constants.reflections.blend;
    TextureRef resolved = { constants.reflections.resolved };
    StorageRef light = { blend.light, 0u };
    float4 current = resolved.Load(thread.xy, 0u);
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, pixel, surface) || blend.hasHistory == 0u)
    {
        light.Store(thread.xy, current);
        return;
    }

    Neighbourhood neighbourhood = GetNeighbourhood(resolved, thread.xy, size);
    TextureRef history = { blend.lightHistory };
    SamplerRef sampler = { constants.sampler };
    TextureRef velocityTexture = { frame.velocity };
    TextureRef distance = { constants.reflections.distance };
    float3 velocity = velocityTexture.Load(pixel, 0u).xyz;
    float2 surfaceUv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    float2 reflectedUv = GetReflectedHistoryUv(frame, surface, distance.Load(thread.xy, 0u).x);
    float surfaceGap = abs(GetLuminance(history.SampleLevel(sampler, surfaceUv, 0.0).rgb) - GetLuminance(neighbourhood.mean));
    float reflectedGap = abs(GetLuminance(history.SampleLevel(sampler, reflectedUv, 0.0).rgb) - GetLuminance(neighbourhood.mean));
    float2 uv = surfaceGap < reflectedGap ? surfaceUv : reflectedUv;
    light.Store(
        thread.xy,
        BlendHistory(
            constants.depthHistory, constants.sampler, size, blend, current, neighbourhood, velocity.z, uv,
            constants.reflectionHistory, HistorySpread, VarianceBlend));
}
