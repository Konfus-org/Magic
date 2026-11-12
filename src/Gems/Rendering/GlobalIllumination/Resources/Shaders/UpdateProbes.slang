// The probe grid's light brought up to date each frame (Majercik et al., "Dynamic Diffuse Global Illumination with
// Ray-Traced Irradiance Fields", 2019): each probe asks for rays as its light still changes, and the probes are given
// them in turn while they fit in a budget for all of them; each ray is traced into the world and lit where it lands by
// one light picked at random and by the probes themselves, and each probe given rays blends them into its light's
// harmonics and its map of distances, moving away from a surface it is near. The pass header's x is the frame's
// buffer, which holds the lights and IndirectLight, which holds the grid; its y is the GI buffer, which holds the
// update's own buffers.
module "UpdateProbes";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Light.Probes;
import Light.Indirect;
import Traced;
import Sky.ISky;
import Trace.HitSurface;
import Scene.Models;

// The rays the trace runs a group of.
static const uint TraceGroupSize = 64u;

// The rays all the probes trace in a frame, C#'s Probes.RayBudget: at least the most one probe traces.
static const uint ProbeRayBudget = 131072u;

// The most rays a probe traces in a frame, and the step its count of rays moves in, also the fewest it asks for.
static const uint MaxProbeRays = 256u;
static const uint ProbeRayStep = 4u;

// The directions a probe blends its light in, a side of its octahedral map, C#'s Probes.IrradianceTexels.
static const uint IrradianceTexels = 8u;

// How far a probe moves from a surface it is near, as a part of its depth's reach.
static const float KeepDistance = 0.1;

// The ray counts buffer's words: the rays the probes were given this frame in all at 0, then each probe's count, none
// while it waits its turn, and where its rays start among the frame's.
uint GetProbeRayCountOffset(uint probe) { return 4u + probe * 8u; }
uint GetProbeRayStartOffset(uint probe) { return 8u + probe * 8u; }

// The harmonics of a value seen along a unit direction.
ProbeSh ProjectOntoSh(float3 direction, float3 value)
{
    ProbeSh sh;
    sh.c[0] = ShBasis0 * value;
    sh.c[1] = ShBasis1 * direction.y * value;
    sh.c[2] = ShBasis1 * direction.z * value;
    sh.c[3] = ShBasis1 * direction.x * value;
    return sh;
}

static const uint IrradianceTexelCount = IrradianceTexels * IrradianceTexels;
static const uint DepthTexelCount = DepthTexels * DepthTexels;

// How fast a probe's light follows its rays, and its distances and its place theirs.
static const float IrradianceBlend = 0.1;
static const float DepthBlend = 0.02;
static const float OffsetBlend = 0.01;

static const float WeightEpsilon = 0.0001;

// A ray as the trace leaves it for the blends: its direction, the distance to what it hit, -1 where it hit nothing,
// and the light it brought back. Packed as 16-bit floats into a uint4, light past what they hold kept at the most.
struct ProbeRay
{
    float3 direction;
    float depth;
    float3 radiance;
}

uint4 EncodeProbeRay(ProbeRay ray)
{
    return uint4(
        PackHalf(ray.direction.x) | (PackHalf(ray.direction.y) << 16u),
        PackHalf(ray.direction.z) | (PackHalf(ray.depth) << 16u),
        PackHalf(ray.radiance.r) | (PackHalf(ray.radiance.g) << 16u),
        PackHalf(ray.radiance.b)
    );
}

ProbeRay DecodeProbeRay(uint4 packed)
{
    ProbeRay ray;
    ray.direction = float3(UnpackHalf(packed.x), UnpackHalf(packed.x >> 16u), UnpackHalf(packed.y));
    ray.depth = UnpackHalf(packed.y >> 16u);
    ray.radiance = float3(UnpackHalf(packed.z), UnpackHalf(packed.z >> 16u), UnpackHalf(packed.w));
    return ray;
}

// What a probe's texel keeps of its light over time, for a mean that follows change fast and noise slowly: the mean, a
// mean over a short window, the variance, how much the variance slows the mean, and how far the short mean has strayed
// from the mean. Packed as 11, 11 and 10-bit floats and two 16-bit floats into a uint4, the variance as its square root
// so it has the means' range.
struct ProbeVariance
{
    float3 mean;
    float3 shortMean;
    float3 variance;
    float varianceSlowing;
    float inconsistency;
}

uint4 EncodeProbeVariance(ProbeVariance data)
{
    return uint4(
        PackR11G11B10(data.mean),
        PackR11G11B10(data.shortMean),
        PackR11G11B10(sqrt(data.variance)),
        PackHalf(data.varianceSlowing) | (PackHalf(data.inconsistency) << 16u)
    );
}

ProbeVariance DecodeProbeVariance(uint4 packed)
{
    ProbeVariance data;
    data.mean = UnpackR11G11B10(packed.x);
    data.shortMean = UnpackR11G11B10(packed.y);
    data.variance = Pow2(UnpackR11G11B10(packed.z));
    data.varianceSlowing = UnpackHalf(packed.w);
    data.inconsistency = UnpackHalf(packed.w >> 16u);
    return data;
}

// The data moved on by a new value: a mean that catches up as fast as the short mean strays from it beyond the noise,
// with fireflies clipped first (Barré-Brisebois, "A Multi-Scale Mean Estimator", in the SIGGRAPH 2021 course "Global
// Illumination Based on Surfels").
ProbeVariance EstimateMean(ProbeVariance data, float3 value, float shortBlend)
{
    float3 deviation = sqrt(max(1e-5, data.variance));
    value -= max(0.0, value - (0.1 + data.shortMean + deviation * 8.0));

    float3 delta = value - data.shortMean;
    data.shortMean = lerp(data.shortMean, value, shortBlend);
    float3 delta2 = value - data.shortMean;

    // A longer window than the short mean's, so the variance doesn't shrink with it.
    data.variance = lerp(data.variance, delta * delta2, shortBlend * 0.5);
    deviation = sqrt(max(1e-5, data.variance));

    float3 luma = float3(0.299, 0.587, 0.114);
    float relativeDiff = dot(luma, abs(data.mean - data.shortMean) / max(1e-5, deviation));
    data.inconsistency = lerp(data.inconsistency, relativeDiff, 0.08);
    float slowing = clamp(dot(luma, 0.5 * data.shortMean / max(1e-5, deviation)), 1.0 / 32.0, 1.0);
    float catchUp = clamp(smoothstep(0.0, 1.0, relativeDiff * max(0.02, data.inconsistency - 0.2)), 1.0 / 256.0, 1.0);
    catchUp *= data.varianceSlowing;
    data.varianceSlowing = lerp(data.varianceSlowing, slowing, 0.1);
    data.mean = lerp(data.mean, value, saturate(catchUp));
    return data;
}

groupshared uint groupInconsistency;

// A group a probe: as many rays as its light still changes, up to the most, a tenth of that out of view and all of them
// before it first blends its rays, kept as its count.
[shader("compute")]
[numthreads(32, 1, 1)]
void AskProbeRays(uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    FrameConstants frame = GetFrame();
    ProbeGridConstants grid = GetProbeGrid();
    ProbeUpdateConstants update = GetGIConstants().probes;
    uint probe = group.x;
    if (index == 0u)
        groupInconsistency = 0u;

    GroupMemoryBarrierWithGroupSync();
    ArrayRef<uint4> variance = { update.variance };
    float inconsistency = 0.0;
    for (uint texel = index; texel < IrradianceTexelCount; texel += 32u)
    {
        inconsistency = max(inconsistency, DecodeProbeVariance(variance[probe * IrradianceTexelCount + texel]).inconsistency);
    }

    // A float of 0 or more orders as its bits do.
    InterlockedMax(groupInconsistency, asuint(inconsistency));
    GroupMemoryBarrierWithGroupSync();
    if (index != 0u)
        return;

    uint rayCount = uint(saturate(asfloat(groupInconsistency)) * float(MaxProbeRays));
    GpuProbe loaded = LoadProbe(grid, probe);
    float3 position = ToCameraRelative(frame, GetProbePosition(grid, GetProbeCoord(grid, probe), loaded));
    if (!IsSphereInFrustum(frame, position, Max3(grid.cellSize) * 2.0))
        rayCount /= 10u;

    rayCount = clamp((rayCount + ProbeRayStep - 1u) / ProbeRayStep * ProbeRayStep, ProbeRayStep, MaxProbeRays);
    if (!HasBlended(loaded))
        rayCount = MaxProbeRays;

    BufferRef counts = { update.rayCounts };
    counts.Store(GetProbeRayCountOffset(probe), rayCount);
}

// The threads that plan the frame's rays, each over a run of the probes.
static const uint PlanGroupSize = 1024u;

groupshared uint groupRaySums[PlanGroupSize];
groupshared uint groupServed;
groupshared uint groupTraced;

// One group: the probes taken in turn from the one the last frame stopped at, each given the rays it asked for, and
// where they start, while they fit in the budget, and none past it, so every probe has its turn within a few frames;
// the rays given in all, the trace's groups to reach them, and the probe the next frame starts at.
[shader("compute")]
[numthreads(PlanGroupSize, 1, 1)]
void PlanProbeRays(uint index : SV_GroupIndex)
{
    ProbeGridConstants grid = GetProbeGrid();
    ProbeUpdateConstants update = GetGIConstants().probes;
    BufferRef counts = { update.rayCounts };
    BufferRef arguments = { update.rayArguments };
    uint probeCount = grid.dimensions.x * grid.dimensions.y * grid.dimensions.z;
    uint cursor = arguments.Load<uint>(12u) % probeCount;
    uint perThread = (probeCount + PlanGroupSize - 1u) / PlanGroupSize;
    uint first = min(index * perThread, probeCount);
    uint last = min(first + perThread, probeCount);
    uint sum = 0u;
    for (uint turn = first; turn < last; turn++)
    {
        sum += counts.Load<uint>(GetProbeRayCountOffset((cursor + turn) % probeCount));
    }

    if (index == 0u)
    {
        groupServed = 0u;
        groupTraced = 0u;
    }

    // The runs' sums added up over the runs before and their own (Hillis and Steele).
    groupRaySums[index] = sum;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 1u; step < PlanGroupSize; step <<= 1u)
    {
        uint before = index >= step ? groupRaySums[index - step] : 0u;
        GroupMemoryBarrierWithGroupSync();
        groupRaySums[index] += before;
        GroupMemoryBarrierWithGroupSync();
    }

    uint start = groupRaySums[index] - sum;
    uint served = 0u;
    for (uint turn = first; turn < last; turn++)
    {
        uint probe = (cursor + turn) % probeCount;
        uint rayCount = counts.Load<uint>(GetProbeRayCountOffset(probe));
        bool fits = start + rayCount <= ProbeRayBudget;
        counts.Store(GetProbeRayCountOffset(probe), fits ? rayCount : 0u);
        counts.Store(GetProbeRayStartOffset(probe), start);
        start += rayCount;
        if (fits)
        {
            served++;
            InterlockedMax(groupTraced, start);
        }
    }

    InterlockedAdd(groupServed, served);
    GroupMemoryBarrierWithGroupSync();
    if (index != 0u)
        return;

    counts.Store(0u, groupTraced);
    arguments.Store(0u, (groupTraced + TraceGroupSize - 1u) / TraceGroupSize);
    arguments.Store(4u, 1u);
    arguments.Store(8u, 1u);
    arguments.Store(12u, (cursor + groupServed) % probeCount);
}

// A group a probe given rays this frame: an entry each in the ray list, the probe in the low 20 bits and the ray above.
[shader("compute")]
[numthreads(32, 1, 1)]
void ListProbeRays(uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    ProbeUpdateConstants update = GetGIConstants().probes;
    uint probe = group.x;
    BufferRef counts = { update.rayCounts };
    uint rayCount = counts.Load<uint>(GetProbeRayCountOffset(probe));
    uint start = counts.Load<uint>(GetProbeRayStartOffset(probe));
    ArrayRef<uint> list = { update.rayList };
    for (uint ray = index; ray < rayCount; ray += 32u)
    {
        list[start + ray] = probe | (ray << 20u);
    }
}

// Ray i of n spread evenly over the sphere (Keinert et al., "Spherical Fibonacci Mapping", 2015).
float3 GetFibonacciDirection(uint i, uint n)
{
    float goldenRatio = sqrt(5.0) * 0.5 + 0.5;
    float phi = TwoPi * frac(float(i) * (goldenRatio - 1.0));
    float cosTheta = 1.0 - (2.0 * float(i) + 1.0) / float(n);
    float sinTheta = sqrt(saturate(1.0 - cosTheta * cosTheta));
    return float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

// A rotation about a random axis by a random angle, the same for every ray of a frame, so the probes' rays point
// elsewhere each frame (Rodrigues' formula).
float3x3 GetRandomRotation(uint frameIndex)
{
    float3 xi = GetRandom(uint2(0u), frameIndex, 0u);
    float3 axis = SampleUniformSphere(xi.xy);
    float angle = TwoPi * xi.z;
    float s = sin(angle);
    float c = cos(angle);
    float t = 1.0 - c;
    return float3x3(
        t * axis.x * axis.x + c, t * axis.x * axis.y + s * axis.z, t * axis.x * axis.z - s * axis.y,
        t * axis.x * axis.y - s * axis.z, t * axis.y * axis.y + c, t * axis.y * axis.z + s * axis.x,
        t * axis.x * axis.z + s * axis.y, t * axis.y * axis.z - s * axis.x, t * axis.z * axis.z + c
    );
}

// A thread a ray of the list: traced from its probe, it brings back the sky where it hits nothing, and where it hits,
// what the surface there reflects of the lights and of the probes' light, and gives off.
[shader("compute")]
[numthreads(TraceGroupSize, 1, 1)]
void TraceProbes<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    Lights lights = GetLights();
    ProbeGridConstants grid = GetProbeGrid();
    ProbeUpdateConstants update = GetGIConstants().probes;
    BufferRef counts = { update.rayCounts };
    if (thread.x >= counts.Load<uint>(0u))
        return;

    ArrayRef<uint> list = { update.rayList };
    uint entry = list[thread.x];
    uint probe = entry & 0xFFFFFu;
    uint rayIndex = entry >> 20u;
    float3 origin = GetProbePosition(grid, GetProbeCoord(grid, probe), LoadProbe(grid, probe));
    uint rayCount = counts.Load<uint>(GetProbeRayCountOffset(probe));
    float3 direction = normalize(mul(GetFibonacciDirection(rayIndex, rayCount), GetRandomRotation(frame.frameIndex)));

    RaytracingAccelerationStructure world = GetTracedAcceleration();
    RayHit hit = GetClosestHit(world, origin, direction, 1e30, RAY_FLAG_NONE);
    ProbeRay ray;
    ray.direction = direction;
    if (!hit.isHit)
    {
        ray.depth = -1.0;
        ray.radiance = GetSkyRadiance<S>(lights, direction);
    }
    else
    {
        HitSurface surface = LoadHitSurface(hit, origin, direction);

        // A ray that reached a back is cut short, so a probe sees the inside closer than it is and doesn't leak it.
        ray.depth = surface.isBackface ? hit.distance * 0.9 : hit.distance;
        float3 xi = GetRandom(uint2(thread.x, 0u), frame.frameIndex, 1u);
        float3 light = GetDirectLight<S>(lights, world, surface, xi);
        light += 0.95 * GetProbeIrradiance(grid, surface.position, surface.normal);

        ray.radiance = light * surface.diffuse + surface.emissive;
    }

    ArrayRef<uint4> rays = { update.rays };
    rays[thread.x] = EncodeProbeRay(ray);
}

groupshared float4 groupRays[MaxProbeRays];
groupshared float3 groupRadiance[MaxProbeRays];
groupshared float3 groupMeans[IrradianceTexelCount];
groupshared float2 groupMoments[DepthTexelCount];

// Where border texel i of a probe's depth tile copies from inside it, so a bilinear read at a tile's edge wraps as the
// octahedron does: xy the texel inside the tile, zw the border texel, both from the tile's corner. A side's texel
// copies the mirrored texel of the row or column inside it, and a corner the opposite inner corner.
uint4 GetDepthBorder(uint border)
{
    // The last texel inside the tile, and the far border's.
    uint inner = DepthTexels;
    uint outer = DepthTile - 1u;
    uint i = border % DepthTexels;
    switch (border / DepthTexels)
    {
    case 0u: return uint4(inner - i, 1u, 1u + i, 0u);
    case 1u: return uint4(inner - i, inner, 1u + i, outer);
    case 2u: return uint4(1u, inner - i, 0u, 1u + i);
    case 3u: return uint4(inner, inner - i, outer, 1u + i);
    default: return uint4(inner - (i & 1u) * (inner - 1u), inner - (i >> 1u) * (inner - 1u), (i & 1u) * outer, (i >> 1u) * outer);
    }
}

// The texels of a tile's border: four sides and four corners.
static const uint DepthBorderTexels = 4u * DepthTexels + 4u;

// A group a probe, a thread a ray and a direction of its 16 by 16 map of distances, the first 64 threads also one of
// its 8 by 8 map of light. The light: the probe's rays weighted by how near each is to the direction, followed by the
// mean estimator, then the map projected into the probe's harmonics. The distances: the mean and squared mean of its
// rays' distances, sharply weighted towards the direction, blended slowly into the map, its border copied around it;
// and the probe moved away from what its rays found nearer than it should keep. A probe traces at most a ray a thread.
[shader("compute")]
[numthreads(DepthTexels, DepthTexels, 1)]
void BlendProbes(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    ProbeGridConstants grid = GetProbeGrid();
    ProbeUpdateConstants update = GetGIConstants().probes;
    uint probe = group.x;
    BufferRef counts = { update.rayCounts };
    ArrayRef<uint4> rays = { update.rays };
    uint rayCount = min(counts.Load<uint>(GetProbeRayCountOffset(probe)), MaxProbeRays);
    if (rayCount == 0u)
        return;

    uint start = counts.Load<uint>(GetProbeRayStartOffset(probe));
    bool hasBlended = HasBlended(LoadProbe(grid, probe));
    if (index < rayCount)
    {
        ProbeRay ray = DecodeProbeRay(rays[start + index]);
        groupRays[index] = float4(ray.direction, ray.depth);
        groupRadiance[index] = ray.radiance;
    }

    GroupMemoryBarrierWithGroupSync();

    // The light of the first 64 threads' directions.
    if (index < IrradianceTexelCount)
    {
        uint2 at = uint2(index % IrradianceTexels, index / IrradianceTexels);
        float3 texelDirection = DecodeUnitVectorUnorm((float2(at) + 0.5) / float(IrradianceTexels));
        float3 sum = float3(0.0);
        float totalWeight = 0.0;
        for (uint r = 0u; r < rayCount; r++)
        {
            float weight = saturate(dot(texelDirection, groupRays[r].xyz));
            if (weight > WeightEpsilon)
            {
                sum += groupRadiance[r] * weight;
                totalWeight += weight;
            }
        }

        if (totalWeight > WeightEpsilon)
            sum /= totalWeight;

        ArrayRef<uint4> variance = { update.variance };
        uint texel = probe * IrradianceTexelCount + index;
        ProbeVariance data = DecodeProbeVariance(variance[texel]);
        if (!hasBlended)
        {
            ProbeVariance fresh = {};
            data = fresh;
            data.mean = sum;
            data.shortMean = sum;
            data.inconsistency = 1.0;
        }

        data = EstimateMean(data, sum, IrradianceBlend);
        variance[texel] = EncodeProbeVariance(data);
        groupMeans[index] = data.mean;
    }

    // The distances of every thread's direction, and the push away from what is too near.
    float maxDistance = update.maxDistance;
    float keep = maxDistance * KeepDistance;
    float3 texelDirection = DecodeUnitVectorUnorm((float2(local.xy) + 0.5) / float(DepthTexels));
    float2 moments = float2(0.0);
    float totalWeight = 0.0;
    float3 push = float3(0.0);
    for (uint r = 0u; r < rayCount; r++)
    {
        float4 ray = groupRays[r];
        float depth = ray.w > 0.0 ? clamp(ray.w - 0.01, 0.0, maxDistance) : maxDistance;
        if (depth < keep)
            push -= ray.xyz * (keep - depth);

        float weight = pow(saturate(dot(texelDirection, ray.xyz)), 64.0);
        if (weight > WeightEpsilon)
        {
            moments += float2(depth, depth * depth) * weight;
            totalWeight += weight;
        }
    }

    if (totalWeight > WeightEpsilon)
        moments /= totalWeight;

    uint2 corner = GetProbeDepthTexel(grid, GetProbeCoord(grid, probe)) - 1u;
    StorageRef depthTarget = { grid.depth, 0u };
    if (hasBlended)
        moments = lerp(depthTarget.Load(corner + 1u + local.xy).xy, moments, DepthBlend);

    groupMoments[index] = moments;
    depthTarget.Store(corner + 1u + local.xy, float4(moments, 0.0, 0.0));
    GroupMemoryBarrierWithGroupSync();
    if (index < DepthBorderTexels)
    {
        uint4 border = GetDepthBorder(index);
        uint2 from = border.xy - 1u;
        depthTarget.Store(corner + border.zw, float4(groupMoments[from.x + from.y * DepthTexels], 0.0, 0.0));
    }

    if (index != 0u)
        return;

    // Each texel's light over the sphere into harmonics: the sum of the texels, each covering 4 pi over their count.
    ProbeSh sh = {};
    for (uint i = 0u; i < IrradianceTexelCount; i++)
    {
        uint2 at = uint2(i % IrradianceTexels, i / IrradianceTexels);
        float3 direction = DecodeUnitVectorUnorm((float2(at) + 0.5) / float(IrradianceTexels));
        ProbeSh texelSh = ProjectOntoSh(direction, groupMeans[i]);
        for (uint band = 0u; band < 4u; band++)
        {
            sh.c[band] += texelSh.c[band] * (4.0 * Pi / float(IrradianceTexelCount));
        }
    }

    ArrayRef<GpuProbe> probes = { grid.probes };
    GpuProbe updated = probes[probe];
    for (uint band = 0u; band < 4u; band++)
    {
        updated.radiance[band] = float4(sh.c[band], 0.0);
    }

    float3 limit = grid.cellSize * 0.5;
    float3 offset = hasBlended ? updated.offset.xyz * limit : float3(0.0);
    updated.offset = float4(clamp(lerp(offset, push, OffsetBlend), -limit, limit) / limit, 1.0);
    probes[probe] = updated;
}
