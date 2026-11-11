// The air between the camera and what it sees, filled into a volume of cells over the view, each a part of the
// picture's pixels across and down and a slice of the view away from the camera: each cell's haze and the light it
// scatters towards the camera, the sky's, the sun's and a local light's where a ray through the world reaches each, at
// a point that moves within the cell each frame, blended with the frames before where the cell was then; then summed
// along each column away from the camera, after Wronski, "Volumetric Fog", SIGGRAPH 2014, into what shading reads
// through Light/Air. The pass header's x is the frame's buffer, which holds the lights and the air, y the scattering
// written, z last frame's, 0 when it holds nothing, and w the air.
module "FillAir";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Scene.Shadows;
import Light.Air;
import Light.Lights;

// How much of each frame's cell the blend over frames takes in.
static const float NewWeight = 0.03;

// The directions over the sky a cell asks in turn whether its light gets through.
static const uint SkyDirections = 16u;

// The threads of a group along a side: the cells of one slice it fills.
static const uint Threads = 8u;

// How many of the group's local lights a cell weighs to pick the one it traces.
static const uint Candidates = 8u;

// The local lights whose reach touches the group's cells, by their index in the light table.
groupshared uint groupLightCount;
groupshared uint groupLights[MaxLights];

// A thread a cell: its haze, as the part of a metre's light it scatters, in alpha, and in rgb the light it scatters
// towards the camera per part scattered.
[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterAir(uint3 thread : SV_DispatchThreadID, uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    PassHeader header = GetPassHeader();
    StorageVolumeRef scattering = { header.y, 0u };
    uint3 size = scattering.GetSize();
    FrameConstants frame = GetFrame();
    Air air = GetAir();
    Lights lights = GetLights();

    // The local lights whose reach touches the box around the group's cells, in the camera-relative world, listed in
    // groupLights before any thread past the volume's edge leaves.
    if (index == 0u)
        groupLightCount = 0u;

    float near = ToAirDistance(float(group.z) / float(size.z), air.distance);
    float far = ToAirDistance(float(group.z + 1u) / float(size.z), air.distance);
    float3 low = float3(1e30);
    float3 high = float3(-1e30);
    for (uint c = 0u; c < 8u; c++)
    {
        uint2 corner = min((group.xy + uint2(c & 1u, (c >> 1u) & 1u)) * Threads, size.xy);
        float3 toCorner = ReconstructRelative(frame, float2(corner) / float2(size.xy), 1.0);
        float3 at = NormalizeSafe(toCorner, float3(0.0, 0.0, -1.0)) * (c < 4u ? near : far);
        low = min(low, at);
        high = max(high, at);
    }

    GroupMemoryBarrierWithGroupSync();
    for (uint i = index; i < min(lights.count, MaxLights); i += Threads * Threads)
    {
        GpuLight light = LoadLight(lights.table, i);
        if (IsSphereTouchingBox(Relative(frame, light.position), GetLightReach(light), low, high))
        {
            uint slot;
            InterlockedAdd(groupLightCount, 1u, slot);
            groupLights[slot] = i;
        }
    }

    GroupMemoryBarrierWithGroupSync();
    if (any(thread >= size))
        return;

    float2 uv = GetTexelCenterUv(thread.xy, size.xy);
    float3 random = GetRandom(thread.xy, frame.frameIndex, thread.z);
    float distance = ToAirDistance((float(thread.z) + random.x) / float(size.z), air.distance);
    float3 direction = NormalizeSafe(ReconstructRelative(frame, uv, 1.0), float3(0.0, 0.0, -1.0));
    float3 world = frame.cameraPosition.xyz + direction * distance;

    // The sun's light reaches the cell through what stands in its way, where rays see the world, and the sky's through
    // the way out along a direction over it, as the open sky's light weighs it, so a room's air is lit only through
    // its openings: one of 16 spread over the sky, a frame each, turned by the cell's own amount, new each 16 frames
    // (Cranley and Patterson, "Randomization of Number Theoretic Methods for Multiple Integration", 1976).
    float pdf;
    float2 turn = GetRandom(thread.xy, frame.frameIndex / SkyDirections, thread.z).yz;
    float2 spread = frac(GetHammersley(frame.frameIndex % SkyDirections, SkyDirections) + turn);
    float3 up = ToWorld(CreateBasis(float3(0.0, 1.0, 0.0)), SampleCosineHemisphere(spread, pdf));
    float3 sunThrough = GetTransmittance(world, lights.sunDirection.xyz, 1e6)
        * GetCloudShadow(air, world, lights.sunDirection.xyz, frame.timeSec);
    float3 skyThrough = GetTransmittance(world, up, 1e6);

    // The light the local lights scatter towards the camera, per part scattered: one of the group's, picked among
    // Candidates of them in a row from a random one by how much each would give unshadowed, with a ray towards a random
    // point of it, weighted as all the group's lights would be (Talbot et al., "Importance Resampling for Global
    // Illumination", 2005).
    float2 pick = GetRandom(thread.xy, frame.frameIndex, thread.z + AirSlices).xy;
    uint total = groupLightCount;
    uint count = min(total, Candidates);
    uint first = min(uint(pick.x * float(total)), total - 1u);
    float weights[Candidates];
    float sum = 0.0;
    for (uint j = 0u; j < count; j++)
    {
        GpuLight candidate = LoadLight(lights.table, groupLights[(first + j) % total]);
        LightSample sample = SampleLight(candidate, world, float3(0.0));
        weights[j] = GetLuminance(sample.irradiance) * GetAirPhase(air.anisotropy, dot(direction, sample.direction));
        sum += weights[j];
    }

    float3 localLight = float3(0.0);
    if (sum > 0.0)
    {
        uint picked = 0u;
        float target = pick.y * sum;
        while (picked + 1u < count && target >= weights[picked])
        {
            target -= weights[picked];
            picked++;
        }

        GpuLight light = LoadLight(lights.table, groupLights[(first + picked) % total]);
        LightSample sample = SampleLight(light, world, float3(0.0));
        LightRay ray = CreateLightRay(world, SampleLightPoint(light, world, random.yz));
        float3 through = GetTransmittance(world, ray.direction, ray.distance);
        float phase = GetAirPhase(air.anisotropy, dot(direction, sample.direction));
        float weight = sum / max(weights[picked], 1e-12) * float(total) / float(count);
        localLight = air.color * sample.irradiance * phase * through * weight;
    }

    float3 radiance = GetAirRadiance(air, lights, direction, skyThrough, sunThrough) + localLight;
    float4 cell = float4(radiance, GetHazeDensity(air, world.y));

    // Last frame's cell where this one's centre was then, unless that was off its view or past its reach: the centre,
    // not this frame's point, so a still view reads its own cell rather than a different blend of its neighbours each
    // frame.
    if (header.z != 0u)
    {
        float3 center = frame.cameraPosition.xyz
            + direction * ToAirDistance((float(thread.z) + 0.5) / float(size.z), air.distance);
        float3 previous = center - frame.previousCameraPosition.xyz;
        float4 clip = mul(float4(previous, 1.0), frame.previousViewProjection);
        float3 at = float3(ToUv(clip.xy / max(clip.w, Epsilon)), ToAirDepth(length(previous), air.distance));
        if (clip.w > 0.0 && all(at >= 0.0) && all(at <= 1.0))
        {
            VolumeRef history = { header.z };
            SamplerRef sampler = { air.sampler };
            cell = lerp(history.SampleLevel(sampler, at, 0.0), cell, NewWeight);
        }
    }

    scattering.Store(thread, cell);
}

// A thread a column of cells: the light scattered towards the camera from it up to each cell's far side, and how much
// of what is behind gets through, each cell's haze taken as even through its slice.
[shader("compute")]
[numthreads(8, 8, 1)]
void IntegrateAir(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    StorageVolumeRef scattering = { header.y, 0u };
    StorageVolumeRef integrated = { header.w, 0u };
    uint3 size = scattering.GetSize();
    if (any(thread.xy >= size.xy))
        return;

    Air air = GetAir();
    float3 lit = float3(0.0);
    float through = 1.0;
    float near = 0.0;
    for (uint slice = 0u; slice < size.z; slice++)
    {
        float far = ToAirDistance(float(slice + 1u) / float(size.z), air.distance);

        // The cell as the mean of the 3 by 3 cells of its slice around it, so what few of a cell's rays found evens out
        // over its neighbours.
        float4 cell = float4(0.0);
        for (uint i = 0u; i < 9u; i++)
        {
            int2 at = clamp(int2(thread.xy) + int2(i % 3u, i / 3u) - 1, int2(0), int2(size.xy) - 1);
            cell += scattering.Load(uint3(at, slice));
        }

        cell /= 9.0;
        float sliceThrough = exp(-cell.a * (far - near));
        lit += through * cell.rgb * (1.0 - sliceThrough);
        through *= sliceThrough;
        integrated.Store(uint3(thread.xy, slice), float4(lit, through));
        near = far;
    }
}
