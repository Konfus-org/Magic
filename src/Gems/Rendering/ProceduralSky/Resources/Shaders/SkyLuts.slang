// The atmosphere's three tables, made ahead of each frame: transmittance through the air by height and direction,
// the light every direction scatters again by height and sun, and the sky as seen from the camera's height. The
// pass header's x is the frame's buffer, which holds the lights and the tables' ids.
module "SkyLuts";

import Core.Heap;
import Core.Math;
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
    float3 sky = GetSkyView(transmittance, multi, sampler, direction, lights.sunDirection.xyz, altitude);
    target.Store(thread.xy, float4(sky, 1.0));
}
