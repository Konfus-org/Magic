// The probe grid's light brought up to date each frame (Majercik et al., "Dynamic Diffuse Global Illumination with
// Ray-Traced Irradiance Fields", 2019): each probe is given rays as its light still changes, each ray is traced into the
// world and lit where it lands by one light picked at random and by the probes themselves, and each probe blends its
// rays into its light's harmonics and its map of distances, moving away from a surface it is near. The pass header's x
// is the frame's buffer, which holds the lights, and y the grid's constants.
module "UpdateProbes";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Culling;
import Scene.Lights;
import Scene.Probes;
import Sky.ISky;
import Trace.HitSurface;
import Trace.HitLight;
import Scene.Models;

// The rays the trace runs a group of.
static const uint TraceGroupSize = 64u;

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
groupshared uint groupRayCount;
groupshared uint groupRayStart;

// A group a probe: as many rays as its light still changes, up to the most, a tenth of that out of view and all of them
// on the first update; their count, and an entry each in the ray list, the probe in the low 20 bits and the ray above.
[shader("compute")]
[numthreads(32, 1, 1)]
void AllocateProbeRays(uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    FrameConstants frame = GetFrame();
    ProbeGridConstants grid = GetProbeGrid();
    uint probe = group.x;
    if (index == 0u)
        groupInconsistency = 0u;

    GroupMemoryBarrierWithGroupSync();
    ArrayRef<uint4> variance = { grid.variance };
    float inconsistency = 0.0;
    for (uint texel = index; texel < IrradianceTexelCount; texel += 32u)
    {
        inconsistency = max(inconsistency, DecodeProbeVariance(variance[probe * IrradianceTexelCount + texel]).inconsistency);
    }

    // A float of 0 or more orders as its bits do.
    InterlockedMax(groupInconsistency, asuint(inconsistency));
    GroupMemoryBarrierWithGroupSync();
    if (index == 0u)
    {
        uint rayCount = uint(saturate(asfloat(groupInconsistency)) * float(MaxProbeRays));
        uint3 coord = GetProbeCoord(grid, probe);
        float3 position = Relative(frame, GetProbePosition(grid, coord, LoadProbe(grid, probe)));
        if (!IsSphereInFrustum(frame, position, Max3(grid.cellSize) * 2.0))
            rayCount /= 10u;

        rayCount = clamp((rayCount + ProbeRayStep - 1u) / ProbeRayStep * ProbeRayStep, ProbeRayStep, MaxProbeRays);
        if (grid.hasHistory == 0u)
            rayCount = MaxProbeRays;

        BufferRef counts = { grid.rayCounts };
        counts.Store(GetProbeRayCountOffset(probe), rayCount);
        groupRayCount = rayCount;
        groupRayStart = counts.InterlockedAdd(0u, rayCount);
    }

    GroupMemoryBarrierWithGroupSync();
    ArrayRef<uint> list = { grid.rayList };
    for (uint ray = index; ray < groupRayCount; ray += 32u)
    {
        list[groupRayStart + ray] = probe | (ray << 20u);
    }
}

// The trace's groups for the rays the probes were given.
[shader("compute")]
[numthreads(1, 1, 1)]
void PrepareProbeRays()
{
    ProbeGridConstants grid = GetProbeGrid();
    BufferRef counts = { grid.rayCounts };
    BufferRef arguments = { grid.rayArguments };
    arguments.Store(0u, (counts.Load<uint>(0u) + TraceGroupSize - 1u) / TraceGroupSize);
    arguments.Store(4u, 1u);
    arguments.Store(8u, 1u);
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
    float pdf;
    float3 axis = SampleUniformSphere(xi.xy, pdf);
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
    BufferRef counts = { grid.rayCounts };
    if (thread.x >= counts.Load<uint>(0u))
        return;

    ArrayRef<uint> list = { grid.rayList };
    uint entry = list[thread.x];
    uint probe = entry & 0xFFFFFu;
    uint rayIndex = entry >> 20u;
    float3 origin = GetProbePosition(grid, GetProbeCoord(grid, probe), LoadProbe(grid, probe));
    uint rayCount = counts.Load<uint>(GetProbeRayCountOffset(probe));
    float3 direction = normalize(mul(GetFibonacciDirection(rayIndex, rayCount), GetRandomRotation(frame.frameIndex)));

    AccelerationRef acceleration = { GetModels().acceleration };
    RaytracingAccelerationStructure world = acceleration.Get();
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
        if (grid.hasHistory != 0u)
            light += 0.95 * GetProbeIrradiance(grid, surface.position, surface.normal);

        ray.radiance = light * surface.diffuse + surface.emissive;
    }

    ArrayRef<uint4> rays = { grid.rays };
    rays[probe * MaxProbeRays + rayIndex] = EncodeProbeRay(ray);
}

groupshared float4 groupRays[DepthTexelCount];
groupshared float3 groupRadiance[IrradianceTexelCount];
groupshared float2 groupMoments[DepthTexelCount];

// A group a probe, a thread a direction of its 8 by 8 map: the probe's rays weighted by how near each is to the
// direction, followed by the mean estimator, then the map projected into the probe's harmonics.
[shader("compute")]
[numthreads(IrradianceTexels, IrradianceTexels, 1)]
void BlendProbeIrradiance(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    ProbeGridConstants grid = GetProbeGrid();
    uint probe = group.x;
    float3 texelDirection = DecodeOctahedral((float2(local.xy) + 0.5) / float(IrradianceTexels) * 2.0 - 1.0);
    BufferRef counts = { grid.rayCounts };
    ArrayRef<uint4> rays = { grid.rays };
    uint rayCount = min(counts.Load<uint>(GetProbeRayCountOffset(probe)), MaxProbeRays);
    float3 sum = float3(0.0);
    float totalWeight = 0.0;
    for (uint first = 0u; first < rayCount; first += IrradianceTexelCount)
    {
        uint batch = min(IrradianceTexelCount, rayCount - first);
        if (index < batch)
        {
            ProbeRay ray = DecodeProbeRay(rays[probe * MaxProbeRays + first + index]);
            groupRays[index] = float4(ray.direction, ray.depth);
            groupRadiance[index] = ray.radiance;
        }

        GroupMemoryBarrierWithGroupSync();
        for (uint r = 0u; r < batch; r++)
        {
            float weight = saturate(dot(texelDirection, groupRays[r].xyz));
            if (weight > WeightEpsilon)
            {
                sum += groupRadiance[r] * weight;
                totalWeight += weight;
            }
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (totalWeight > WeightEpsilon)
        sum /= totalWeight;

    ArrayRef<uint4> variance = { grid.variance };
    uint texel = probe * IrradianceTexelCount + index;
    ProbeVariance data = DecodeProbeVariance(variance[texel]);
    if (grid.hasHistory == 0u)
    {
        ProbeVariance fresh = {};
        data = fresh;
        data.mean = sum;
        data.shortMean = sum;
        data.inconsistency = 1.0;
    }

    data = EstimateMean(data, sum, IrradianceBlend);
    variance[texel] = EncodeProbeVariance(data);
    groupRadiance[index] = data.mean;
    GroupMemoryBarrierWithGroupSync();
    if (index != 0u)
        return;

    // Each texel's light over the sphere into harmonics: the sum of the texels, each covering 4 pi over their count.
    ProbeSh sh = {};
    for (uint i = 0u; i < IrradianceTexelCount; i++)
    {
        uint2 at = uint2(i % IrradianceTexels, i / IrradianceTexels);
        float3 direction = DecodeOctahedral((float2(at) + 0.5) / float(IrradianceTexels) * 2.0 - 1.0);
        ProbeSh texelSh = ProjectOntoSh(direction, groupRadiance[i]);
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

    probes[probe] = updated;
}

// Where each border texel of a probe's depth tile copies from inside it, so a bilinear read at a tile's edge wraps as
// the octahedron does: xy the texel inside the tile, zw the border texel, both from the tile's corner.
static const uint4 DepthBorder[68] = {
    uint4(16, 1, 1, 0), uint4(15, 1, 2, 0), uint4(14, 1, 3, 0), uint4(13, 1, 4, 0), uint4(12, 1, 5, 0),
    uint4(11, 1, 6, 0), uint4(10, 1, 7, 0), uint4(9, 1, 8, 0), uint4(8, 1, 9, 0), uint4(7, 1, 10, 0),
    uint4(6, 1, 11, 0), uint4(5, 1, 12, 0), uint4(4, 1, 13, 0), uint4(3, 1, 14, 0), uint4(2, 1, 15, 0),
    uint4(1, 1, 16, 0), uint4(16, 16, 1, 17), uint4(15, 16, 2, 17), uint4(14, 16, 3, 17), uint4(13, 16, 4, 17),
    uint4(12, 16, 5, 17), uint4(11, 16, 6, 17), uint4(10, 16, 7, 17), uint4(9, 16, 8, 17), uint4(8, 16, 9, 17),
    uint4(7, 16, 10, 17), uint4(6, 16, 11, 17), uint4(5, 16, 12, 17), uint4(4, 16, 13, 17), uint4(3, 16, 14, 17),
    uint4(2, 16, 15, 17), uint4(1, 16, 16, 17), uint4(1, 16, 0, 1), uint4(1, 15, 0, 2), uint4(1, 14, 0, 3),
    uint4(1, 13, 0, 4), uint4(1, 12, 0, 5), uint4(1, 11, 0, 6), uint4(1, 10, 0, 7), uint4(1, 9, 0, 8),
    uint4(1, 8, 0, 9), uint4(1, 7, 0, 10), uint4(1, 6, 0, 11), uint4(1, 5, 0, 12), uint4(1, 4, 0, 13),
    uint4(1, 3, 0, 14), uint4(1, 2, 0, 15), uint4(1, 1, 0, 16), uint4(16, 16, 17, 1), uint4(16, 15, 17, 2),
    uint4(16, 14, 17, 3), uint4(16, 13, 17, 4), uint4(16, 12, 17, 5), uint4(16, 11, 17, 6), uint4(16, 10, 17, 7),
    uint4(16, 9, 17, 8), uint4(16, 8, 17, 9), uint4(16, 7, 17, 10), uint4(16, 6, 17, 11), uint4(16, 5, 17, 12),
    uint4(16, 4, 17, 13), uint4(16, 3, 17, 14), uint4(16, 2, 17, 15), uint4(16, 1, 17, 16), uint4(16, 16, 0, 0),
    uint4(1, 16, 17, 0), uint4(16, 1, 0, 17), uint4(1, 1, 17, 17),
};

// A group a probe, a thread a direction of its 16 by 16 map of distances: the mean and squared mean of its rays'
// distances, sharply weighted towards the direction, blended slowly into the map, its border copied around it; and the
// probe moved away from what its rays found nearer than it should keep.
[shader("compute")]
[numthreads(DepthTexels, DepthTexels, 1)]
void BlendProbeDepth(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    ProbeGridConstants grid = GetProbeGrid();
    uint probe = group.x;
    uint3 coord = GetProbeCoord(grid, probe);
    float maxDistance = grid.maxDistance;
    float keep = maxDistance * KeepDistance;
    float3 texelDirection = DecodeOctahedral((float2(local.xy) + 0.5) / float(DepthTexels) * 2.0 - 1.0);
    BufferRef counts = { grid.rayCounts };
    ArrayRef<uint4> rays = { grid.rays };
    uint rayCount = min(counts.Load<uint>(GetProbeRayCountOffset(probe)), MaxProbeRays);
    float2 moments = float2(0.0);
    float totalWeight = 0.0;
    float3 push = float3(0.0);
    for (uint first = 0u; first < rayCount; first += DepthTexelCount)
    {
        uint batch = min(DepthTexelCount, rayCount - first);
        if (index < batch)
        {
            ProbeRay ray = DecodeProbeRay(rays[probe * MaxProbeRays + first + index]);
            groupRays[index] = float4(ray.direction, ray.depth);
        }

        GroupMemoryBarrierWithGroupSync();
        for (uint r = 0u; r < batch; r++)
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

        GroupMemoryBarrierWithGroupSync();
    }

    if (totalWeight > WeightEpsilon)
        moments /= totalWeight;

    uint2 corner = GetProbeDepthTexel(grid, coord) - 1u;
    StorageRef depthTarget = { grid.depth, 0u };
    if (grid.hasHistory != 0u)
        moments = lerp(depthTarget.Load(corner + 1u + local.xy).xy, moments, DepthBlend);

    groupMoments[index] = moments;
    depthTarget.Store(corner + 1u + local.xy, float4(moments, 0.0, 0.0));
    GroupMemoryBarrierWithGroupSync();
    for (uint border = index; border < 68u; border += DepthTexelCount)
    {
        uint2 from = DepthBorder[border].xy - 1u;
        depthTarget.Store(corner + DepthBorder[border].zw, float4(groupMoments[from.x + from.y * DepthTexels], 0.0, 0.0));
    }

    if (index != 0u)
        return;

    ArrayRef<GpuProbe> probes = { grid.probes };
    GpuProbe updated = probes[probe];
    float3 limit = grid.cellSize * 0.5;
    float3 offset = grid.hasHistory != 0u ? updated.offset.xyz * limit : float3(0.0);
    updated.offset = float4(clamp(lerp(offset, push, OffsetBlend), -limit, limit) / limit, 0.0);
    probes[probe] = updated;
}
