// The air between the camera and what it sees, filled into a volume of cells over the view, each a part of the
// picture's pixels across and down and a slice of the view away from the camera: each cell's haze and the light it
// scatters towards the camera, the sky's and the sun's where a ray through the world reaches each, at a point that moves
// within the cell each frame, blended with the frames before where the cell was then; then summed along each column
// away from the camera, after Wronski, "Volumetric Fog", SIGGRAPH 2014, into what shading reads through Light/Air. The
// pass header's x is the frame's buffer, which holds the lights and the air, y the scattering written, z last frame's,
// 0 when it holds nothing, and w the air.
module "FillAir";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Scene.Shadows;
import Light.Air;

// How much of each frame's cell the blend over frames takes in.
static const float NewWeight = 0.03;

// The directions over the sky a cell asks in turn whether its light gets through.
static const uint SkyDirections = 16u;

// A thread a cell: its haze, as the part of a metre's light it scatters, in alpha, and in rgb the light it scatters
// towards the camera per part scattered.
[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterAir(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    StorageVolumeRef scattering = { header.y, 0u };
    uint3 size = scattering.GetSize();
    if (any(thread >= size))
        return;

    FrameConstants frame = GetFrame();
    Air air = GetAir();
    Lights lights = GetLights();
    float2 uv = (float2(thread.xy) + 0.5) / float2(size.xy);
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
    float3 sunThrough = GetTransmittance(world, lights.sunDirection.xyz, 1e6);
    float3 skyThrough = GetTransmittance(world, up, 1e6);

    float3 radiance = GetAirRadiance(air, lights, direction, skyThrough, sunThrough);
    float4 cell = float4(radiance, GetHazeDensity(air, world.y));

    // Last frame's cell where this one's point was then, unless that was off its view or past its reach.
    if (header.z != 0u)
    {
        float3 previous = world - frame.previousCameraPosition.xyz;
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

// A cell as the mean of the 3 by 3 cells of its slice around it, so what few of a cell's rays found evens out over its
// neighbours.
float4 LoadAveraged(StorageVolumeRef scattering, uint3 size, uint3 cell)
{
    float4 sum = float4(0.0);
    for (uint i = 0u; i < 9u; i++)
    {
        int2 at = clamp(int2(cell.xy) + int2(i % 3u, i / 3u) - 1, int2(0), int2(size.xy) - 1);
        sum += scattering.Load(uint3(at, cell.z));
    }

    return sum / 9.0;
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
        float4 cell = LoadAveraged(scattering, size, uint3(thread.xy, slice));
        float sliceThrough = exp(-cell.a * (far - near));
        lit += through * cell.rgb * (1.0 - sliceThrough);
        through *= sliceThrough;
        integrated.Store(uint3(thread.xy, slice), float4(lit, through));
        near = far;
    }
}
