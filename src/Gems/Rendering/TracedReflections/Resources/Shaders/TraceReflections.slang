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
import Scene.Materials;
import Scene.Vertices;
import Surface.Surface;
import Material.IMaterial;

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
    // What each traced pixel's ray landed on, a ReflectionHit a traced pixel, row by row.
    public uint hits;
    // The light of each traced pixel's ray from the screen, times how much it counts, which LightReflectionHits adds to.
    public uint screen;
    public TracedBlendConstants blend;
}

// What a reflection ray landed on, as the trace writes it, and that surface as its material's own code evaluates it
// there. The instance is NoHit where the ray landed on nothing that it lights.
struct ReflectionHit
{
    uint instance;
    uint mesh;
    uint triangle;
    float2 barycentrics;
    float3 baseColor;
    float metalness;
    float3 emissive;
    float roughness;
    // Unit, in the world, facing the ray.
    float3 normal;
}

static const uint NoHit = 0xFFFFFFFFu;

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
    // Last frame's lit picture, which the screen trace reads, 0 where there is none.
    public uint picture;
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
    StorageRef screenLight = { reflections.screen, 0u };
    ArrayRef<ReflectionHit> hits = { reflections.hits };
    uint row = thread.y * constants.tracedSize.x + thread.x;
    ReflectionHit record = (ReflectionHit)0;
    record.instance = NoHit;
    ReflectingSurface surface;
    if (!LoadReflectingSurface(frame, GetTracedPixel(frame, thread.xy, constants.tracedSize), surface))
    {
        hits[row] = record;
        traced.Store(thread.xy, float4(0.0, 0.0, 0.0, constants.reflectionRayRange));
        rays.Store(thread.xy, float4(0.0));
        screenLight.Store(thread.xy, float4(0.0));
        return;
    }

    float4 ray = SampleGgxReflection(
        surface.normal, surface.view, surface.roughness, GetRandom(thread.xy, frame.frameIndex, 1u).xy);

    // A ray drawn below the surface, as a surface seen edge-on draws often, is mirrored above it: else it lands on
    // the back of its own mesh and reflects black.
    ray.xyz -= 2.0 * min(dot(ray.xyz, surface.normal), 0.0) * surface.normal;
    // The screen's light where the ray lands on it. The ray is traced through the world only where that counts for
    // less than all, and its light is blended with the screen's by how much the screen's counts.
    ScreenHit screen = TraceScreen(
        frame, constants, surface.relative + surface.normal * GetRayOffset(frame, surface.relative), ray.xyz, thread.xy);
    screenLight.Store(thread.xy, float4(screen.light * screen.weight, 0.0));
    rays.Store(thread.xy, ray);
    if (screen.weight >= 1.0)
    {
        hits[row] = record;
        // LightReflectionHits skips a ray that landed on nothing that it lights, so the screen's light is all of it.
        traced.Store(thread.xy, float4(screen.light, screen.distance));
        return;
    }

    float3 world = surface.relative + frame.cameraPosition.xyz;
    RaytracingAccelerationStructure scene = GetTracedAcceleration();
    // A ray that lands on a back face is inside a mesh, as a ray from the seam between two meshes is: it reflects
    // black, not the sky beyond the wall.
    RayHit hit = GetSurfaceHit(
        frame, scene, world, surface.relative, surface.normal, ray.xyz, constants.reflectionRayRange);
    // A ray that lands on a surface keeps what gets through to it: ShadeReflectionHits evaluates the surface's material
    // and LightReflectionHits lights it.
    float4 light = float4(GetSkyRadiance<S>(GetLights(), ray.xyz) * hit.through, constants.reflectionRayRange);
    if (hit.isHit && !hit.isFrontFace)
        light = float4(0.0, 0.0, 0.0, hit.distance);
    else if (hit.isHit)
    {
        light = float4(hit.through, hit.distance);
        record.instance = hit.instance;
        record.mesh = hit.mesh;
        record.triangle = hit.triangle;
        record.barycentrics = hit.barycentrics;
    }

    // What the traced light counts for rides in what gets through, which the hits' light multiplies, and the screen's
    // light is added after.
    float3 own = record.instance == NoHit ? light.rgb * (1.0 - screen.weight) + screen.light * screen.weight
        : light.rgb * (1.0 - screen.weight);
    hits[row] = record;
    traced.Store(thread.xy, float4(own, lerp(light.a, screen.distance, screen.weight)));
}

// PROTO: the screen trace's steps, the refining steps after a step passes behind the screen's depth, and the part of
// the screen at its edges where the screen's light gives way to the traced light.
static const uint ScreenSteps = 40u;
static const uint ScreenRefineSteps = 5u;
static const float ScreenEdge = 0.08;
// PROTO: how far from across the view a ray must point away from the camera before the screen's light counts in full.
// A ray that comes back towards the camera lands on a side of a surface that the screen does not show.
static const float ScreenFacing = 0.2;

// The screen point and reversed depth of a camera-relative point.
float3 GetScreenPoint(FrameConstants frame, float3 relative)
{
    float4 clip = mul(float4(relative, 1.0), frame.viewProjection);
    return float3(ToUv(clip.xy / clip.w), clip.z / clip.w);
}

// Where a ray lands on the screen: last frame's light there, how much it counts, and how far the ray went.
struct ScreenHit
{
    float3 light;
    float weight;
    float distance;
}

// A ray from a camera-relative point marched over the screen's depth, in steps whose start is dithered by the pixel
// alone, so a still view marches the same each frame. Where it passes behind the depth within one step, on a surface
// that faces it, its light is last frame's lit picture where that surface was, with in weight how much it counts: none
// off the screen, behind a surface thicker than the step, or without last frame's picture, and less near the screen's
// edge.
ScreenHit TraceScreen(FrameConstants frame, ReflectionConstants constants, float3 relative, float3 direction, uint2 thread)
{
    ScreenHit none = { float3(0.0), 0.0, 0.0 };
    float facing = saturate(dot(direction, normalize(relative)) / ScreenFacing);
    if (constants.picture == 0u || facing <= 0.0)
        return none;

    // The ray's end, short of the near plane where it comes towards the camera.
    float range = constants.reflectionRayRange;
    float4 startClip = mul(float4(relative, 1.0), frame.viewProjection);
    float4 endClip = mul(float4(relative + direction * range, 1.0), frame.viewProjection);
    if (endClip.w < frame.near)
        range *= (startClip.w - frame.near) / max(startClip.w - endClip.w, Epsilon);

    float3 start = GetScreenPoint(frame, relative);
    float3 end = GetScreenPoint(frame, relative + direction * range);
    // The end pulled back to the screen's edge, so every step lands on it.
    float2 move = end.xy - start.xy;
    float2 room = select(move > 0.0, 1.0 - start.xy, -start.xy) / select(abs(move) > Epsilon, move, Epsilon);
    float reach = saturate(min(min(room.x, room.y), 1.0));
    end = lerp(start, end, reach);

    TextureRef depthTexture = { frame.depth };
    uint2 size = uint2(frame.width, frame.height);
    float dither = frac(52.9829189 * frac(dot(float2(thread), float2(0.06711056, 0.00583715))));
    float last = 0.0;
    float hitAt = -1.0;
    for (uint i = 1u; i <= ScreenSteps && hitAt < 0.0; i++)
    {
        float t = (float(i) - dither) / float(ScreenSteps);
        float3 at = lerp(start, end, t * t);
        uint2 pixel = min(uint2(at.xy * float2(size)), size - 1u);
        float rayDepth = ToViewDepth(at.z, frame.near);
        float sceneDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);
        if (rayDepth > sceneDepth)
        {
            // A step that is further behind the surface than the step itself is long passed behind it, not into it. A
            // margin past the step let a ray that runs under a ball, behind the ball's own near side, land on that side.
            float stepDepth = abs(rayDepth - ToViewDepth(lerp(start, end, last * last).z, frame.near));
            if (rayDepth - sceneDepth > stepDepth)
                return none;

            hitAt = t;
        }
        else
            last = t;
    }

    if (hitAt < 0.0)
        return none;

    // The hit narrowed between the last step in front and the first behind.
    float front = last;
    float behind = hitAt;
    for (uint j = 0u; j < ScreenRefineSteps; j++)
    {
        float t = 0.5 * (front + behind);
        float3 at = lerp(start, end, t * t);
        uint2 pixel = min(uint2(at.xy * float2(size)), size - 1u);
        if (ToViewDepth(at.z, frame.near) > ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near))
            behind = t;
        else
            front = t;
    }

    float2 uv = lerp(start, end, behind * behind).xy;
    uint2 pixel = min(uint2(uv * float2(size)), size - 1u);
    if (dot(LoadNormal(frame.gbufferNormal, pixel), direction) > 0.0)
        return none;

    TextureRef velocity = { frame.velocity };
    float2 previous = uv + velocity.Load(pixel, 0u).xy;
    float2 edge = min(min(uv, 1.0 - uv), min(previous, 1.0 - previous));
    float weight = saturate(min(edge.x, edge.y) / ScreenEdge) * facing;
    if (weight <= 0.0)
        return none;

    TextureRef picture = { constants.picture };
    SamplerRef sampler = { constants.sampler };
    float3 landed = ReconstructRelative(frame, uv, depthTexture.Load(pixel, 0u).x);
    ScreenHit hit = { picture.SampleLevel(sampler, previous, 0.0).rgb, weight, length(landed - relative) };
    return hit;
}

// The corners of the triangle a ray landed on, in the world for its instance.
struct HitTriangle
{
    float3 positions[3];
    float3 normals[3];
    float3 tangents[3];
    float tangentSign;
    float2 uvs[3];
}

HitTriangle LoadHitTriangle(Models models, ReflectionHit hit, GpuInstance instance, GpuMesh mesh)
{
    HitTriangle corners;
    uint first = mesh.indexOffset + hit.triangle * 3u;
    for (uint i = 0u; i < 3u; i++)
    {
        Vertex vertex = LoadVertex(models.vertices, LoadIndex(models.indices, first + i));
        corners.positions[i] = ToWorld(instance.world, vertex.position);
        corners.normals[i] = vertex.normal;
        corners.tangents[i] = vertex.tangent;
        corners.uvs[i] = vertex.uv;
        corners.tangentSign = vertex.tangentSign;
    }

    return corners;
}

float3 GetBarycentricWeights(float2 barycentrics)
{
    return float3(1.0 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
}

// A thread a traced pixel whose ray landed on a surface that the pass's material shader, numbered in the pass header's
// z, draws: the material's own code evaluated at the hit's texture coordinates, as the screen draws that surface. A ray
// may land on any material, and a pass can only call the one it is specialised to, as AverageMaterials is.
[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeReflectionHits<M : IMaterial>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ReflectionConstants constants = GetReflectionConstants();
    if (any(thread.xy >= constants.tracedSize))
        return;

    ArrayRef<ReflectionHit> hits = { constants.reflections.hits };
    uint row = thread.y * constants.tracedSize.x + thread.x;
    ReflectionHit hit = hits[row];
    if (hit.instance == NoHit)
        return;

    Models models = GetModels();
    GpuInstance instance = LoadInstance(models.instances, hit.instance);
    GpuMesh mesh = LoadMesh(models.meshes, hit.mesh);
    uint material = GetMaterial(instance, mesh.materialSlot);
    if (LoadMaterialShader(models.materials, material) != GetPassHeader().z)
        return;

    HitTriangle corners = LoadHitTriangle(models, hit, instance, mesh);
    float3 weights = GetBarycentricWeights(hit.barycentrics);
    float3 position = corners.positions[0] * weights.x + corners.positions[1] * weights.y
        + corners.positions[2] * weights.z;
    TextureRef rays = { constants.reflections.rays };
    TextureRef traced = { constants.reflections.traced };
    float3 direction = NormalizeSafe(rays.Load(thread.xy, 0u).xyz, float3(0.0, 1.0, 0.0));
    float3 normal = ToWorldNormal(
        instance.world, corners.normals[0] * weights.x + corners.normals[1] * weights.y + corners.normals[2] * weights.z);
    float facing = dot(normal, direction) > 0.0 ? -1.0 : 1.0;

    // PROTO: the texture is sampled over the width of a cone from the camera through a traced pixel, as long as the
    // view's distance to the reflecting surface and the ray's together, by how much of the texture a metre of the
    // triangle holds. It does not widen with the reflecting surface's roughness or with how slanted the hit is.
    float2 uvEdge0 = corners.uvs[1] - corners.uvs[0];
    float2 uvEdge1 = corners.uvs[2] - corners.uvs[0];
    float worldArea = length(cross(corners.positions[1] - corners.positions[0], corners.positions[2] - corners.positions[0]));
    float uvArea = abs(uvEdge0.x * uvEdge1.y - uvEdge0.y * uvEdge1.x);
    float uvPerMetre = sqrt(uvArea / max(worldArea, 1e-12));
    float rayLength = traced.Load(thread.xy, 0u).a;
    float3 viewPoint = position - frame.cameraPosition.xyz;
    float coneWidth = (length(viewPoint - direction * rayLength) + rayLength) / frame.pixelsPerRadian
        * float(frame.width) / float(constants.tracedSize.x);

    SurfaceInputs inputs;
    inputs.position = viewPoint;
    inputs.normal = normal * facing;
    float3 tangent = ToWorldDirection(
        instance.world, corners.tangents[0] * weights.x + corners.tangents[1] * weights.y + corners.tangents[2] * weights.z);
    inputs.tangent = NormalizeSafe(tangent - inputs.normal * dot(tangent, inputs.normal), float3(1.0, 0.0, 0.0));
    inputs.tangentSign = corners.tangentSign * facing;
    inputs.uv = corners.uvs[0] * weights.x + corners.uvs[1] * weights.y + corners.uvs[2] * weights.z;
    inputs.uvDdx = float2(coneWidth * uvPerMetre, 0.0);
    inputs.uvDdy = float2(0.0, coneWidth * uvPerMetre);
    M evaluated = LoadMaterialParams<M>(models.materials, material);
    SurfaceOutputs surface = evaluated.Evaluate(inputs);

    hit.baseColor = surface.baseColor;
    hit.metalness = surface.metalness;
    hit.emissive = surface.emissive;
    hit.roughness = surface.roughness;
    hit.normal = surface.normal;
    hits[row] = hit;
}

// A thread a traced pixel whose ray landed on a surface: its light along the ray, from the material that
// ShadeReflectionHits evaluated. It reflects diffusely what GetHitLight brings, the lights and the probes, and gives
// off its emissive. PROTO: its specular is a rough mirror, its F0 times the probes' light along the ray's reflection
// off it and the light of the sun and the lights that GetHitLight picks by the same xi, so a metal, whose diffuse is
// black, reflects its surroundings and is lit as the thing itself is. The light times what got through to the surface
// takes the place of that in the traced texture.
[shader("compute")]
[numthreads(8, 8, 1)]
void LightReflectionHits<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ReflectionConstants constants = GetReflectionConstants();
    if (any(thread.xy >= constants.tracedSize))
        return;

    ArrayRef<ReflectionHit> hits = { constants.reflections.hits };
    ReflectionHit hit = hits[thread.y * constants.tracedSize.x + thread.x];
    if (hit.instance == NoHit)
        return;

    Models models = GetModels();
    GpuInstance instance = LoadInstance(models.instances, hit.instance);
    HitTriangle corners = LoadHitTriangle(models, hit, instance, LoadMesh(models.meshes, hit.mesh));
    float3 weights = GetBarycentricWeights(hit.barycentrics);
    TextureRef rays = { constants.reflections.rays };
    float3 direction = rays.Load(thread.xy, 0u).xyz;

    HitSurface landed;
    landed.position = corners.positions[0] * weights.x + corners.positions[1] * weights.y
        + corners.positions[2] * weights.z;
    landed.normal = NormalizeSafe(hit.normal, -direction);
    landed.diffuse = hit.baseColor * (1.0 - hit.metalness);
    landed.emissive = hit.emissive;
    landed.isBackface = false;
    float3 f0 = lerp(float3(0.04), hit.baseColor, hit.metalness);

    Lights lights = GetLights();
    ProbeGridConstants grid = GetProbeGrid();
    RaytracingAccelerationStructure scene = GetTracedAcceleration();
    float3 xi = GetRandom(thread.xy, frame.frameIndex, 2u);
    float3 light = GetHitLight<S>(lights, grid, scene, landed, xi)
        + f0 * (GetDirectLight<S>(lights, grid, scene, landed, xi)
            + GetProbeIrradiance(grid, landed.position, reflect(direction, landed.normal)));

    StorageRef traced = { constants.reflections.traced, 0u };
    TextureRef screen = { constants.reflections.screen };
    float4 through = traced.Load(thread.xy);
    traced.Store(thread.xy, float4(light * through.rgb + screen.Load(thread.xy, 0u).rgb, through.a));
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

// How far on last frame's screen the reflection at a surface was from the surface itself: the point the ray's length
// puts behind it along the view, as a mirror shows it, seen from last frame's camera. None while the camera is still,
// so the reflection's history is then the surface's own.
float2 GetReflectionParallax(FrameConstants frame, ReflectingSurface surface, float rayLength)
{
    float3 moved = frame.cameraPosition.xyz - frame.previousCameraPosition.xyz;
    float3 reflected = surface.relative - surface.view * rayLength;
    return GetPreviousScreen(frame, reflected + moved).xy - GetPreviousScreen(frame, surface.relative + moved).xy;
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
    float2 reflectedUv = surfaceUv + GetReflectionParallax(frame, surface, distance.Load(thread.xy, 0u).x);
    float surfaceGap = abs(GetLuminance(history.SampleLevel(sampler, surfaceUv, 0.0).rgb) - GetLuminance(neighbourhood.mean));
    float reflectedGap = abs(GetLuminance(history.SampleLevel(sampler, reflectedUv, 0.0).rgb) - GetLuminance(neighbourhood.mean));
    float2 uv = surfaceGap < reflectedGap ? surfaceUv : reflectedUv;
    light.Store(
        thread.xy,
        BlendHistory(
            constants.depthHistory, constants.sampler, size, blend, current, neighbourhood, velocity.z, uv,
            constants.reflectionHistory, HistorySpread, VarianceBlend));
}
