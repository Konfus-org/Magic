// The atmosphere's three tables, made ahead of each frame: transmittance through the air by height and direction,
// the light every direction scatters again by height and sun, and the sky as seen from the camera's height. The
// pass header's x is the frame's buffer, which holds the lights and the tables' ids. The sky-view pass's y, z and w
// hold the fit to Blender's sky (C#'s SkyCalibration).
module "SkyLuts";

import Core.Heap;
import Core.Math;
import Core.Pass;
import Scene.Lights;
import Sky.Atmosphere;

[shader("compute")]
[numthreads(8, 8, 1)]
void FillSkyTransmittance(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= TransmittanceLutSize))
        return;

    SkyTables tables = GetSkyTables();
    float2 radiusAndCosine = ToRadiusAndCosine(GetTexelCenterUv(thread.xy, TransmittanceLutSize));
    StorageRef target = { tables.transmittance, 0u };
    target.Store(thread.xy, float4(GetAirTransmittance(radiusAndCosine.x, radiusAndCosine.y), 1.0));
}

// The directions' shares of one multi-scattering entry, summed in place by halves.
groupshared float3 luminanceShares[MultiScatteringDirections];
groupshared float3 multiShares[MultiScatteringDirections];

// One group for each entry, one thread for each of its directions.
[shader("compute")]
[numthreads(MultiScatteringDirections, 1, 1)]
void FillSkyMultiScattering(uint3 group : SV_GroupID, uint direction : SV_GroupIndex)
{
    Lights lights = GetLights();
    SkyTables tables = GetSkyTables();
    TextureRef transmittance = { tables.transmittance };
    SamplerRef sampler = { lights.lutSampler };
    float2 uv = GetTexelCenterUv(group.xy, MultiScatteringLutSize);
    float3 luminanceShare;
    float3 multiShare;
    GetMultiScatteringShare(transmittance, sampler, uv, direction, luminanceShare, multiShare);
    luminanceShares[direction] = luminanceShare;
    multiShares[direction] = multiShare;
    GroupMemoryBarrierWithGroupSync();
    for (uint half = MultiScatteringDirections / 2u; half > 0u; half /= 2u)
    {
        if (direction < half)
        {
            luminanceShares[direction] += luminanceShares[direction + half];
            multiShares[direction] += multiShares[direction + half];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (direction != 0u)
        return;

    float3 luminance = luminanceShares[0] / float(MultiScatteringDirections);
    float3 multi = multiShares[0] / float(MultiScatteringDirections);
    StorageRef target = { tables.multiScattering, 0u };
    target.Store(group.xy, float4(luminance / max(1.0 - multi, Epsilon), 1.0));
}

// The gain on each channel that fits the sky to Blender's: the header's two halves of each channel, the gain and its
// change by the light's height, over the light's colour, which Blender's sky does not take.
float3 GetCalibration(Lights lights)
{
    PassHeader header = GetPassHeader();
    uint3 packed = uint3(header.y, header.z, header.w);
    float3 gain = f16tof32(packed & 0xFFFFu) + f16tof32(packed >> 16u) * saturate(lights.sunDirection.y);
    float3 light = lights.sunIlluminance.rgb;
    return gain * (Max3(light) > 0.0 ? Max3(light) / max(light, float3(Epsilon)) : float3(1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FillSkyView(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= SkyViewLutSize))
        return;

    Lights lights = GetLights();
    SkyTables tables = GetSkyTables();
    TextureRef transmittance = { tables.transmittance };
    TextureRef multi = { tables.multiScattering };
    SamplerRef sampler = { lights.lutSampler };
    float altitude = lights.cameraAltitude * 0.001;
    float3 direction = ToSkyViewDirection(GetTexelCenterUv(thread.xy, SkyViewLutSize), altitude);
    StorageRef target = { tables.view, 0u };

    // The sky's brightness and its fit to Blender's sky are in the table, so the sky that is seen, the light it gives
    // the surfaces, the probes and the reflections all take them, and the sun's light, which comes from the
    // transmittance table, does not.
    float3 sky = GetSkyView(transmittance, multi, sampler, direction, lights.sunDirection.xyz, altitude);
    target.Store(thread.xy, float4(sky * tables.brightness * GetCalibration(lights), 1.0));
}
