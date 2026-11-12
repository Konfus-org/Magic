// The atmosphere's three tables, made ahead of each frame: transmittance through the air by height and direction,
// the light every direction scatters again by height and sun, and the sky as seen from the camera's height. The
// pass header's x is the frame's buffer, which holds the lights.
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

    Lights lights = GetLights();
    float2 radiusAndCosine = ToRadiusAndCosine(GetTexelCenterUv(thread.xy, TransmittanceLutSize));
    StorageRef target = { lights.transmittanceLut, 0u };
    target.Store(thread.xy, float4(GetAirTransmittance(radiusAndCosine.x, radiusAndCosine.y), 1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FillSkyMultiScattering(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= MultiScatteringLutSize))
        return;

    Lights lights = GetLights();
    TextureRef transmittance = { lights.transmittanceLut };
    SamplerRef sampler = { lights.lutSampler };
    StorageRef target = { lights.multiScatteringLut, 0u };
    float3 multi = GetMultiScattering(transmittance, sampler, GetTexelCenterUv(thread.xy, MultiScatteringLutSize));
    target.Store(thread.xy, float4(multi, 1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FillSkyView(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= SkyViewLutSize))
        return;

    Lights lights = GetLights();
    TextureRef transmittance = { lights.transmittanceLut };
    TextureRef multi = { lights.multiScatteringLut };
    SamplerRef sampler = { lights.lutSampler };
    float altitude = lights.cameraAltitude * 0.001;
    float3 direction = ToSkyViewDirection(GetTexelCenterUv(thread.xy, SkyViewLutSize), altitude);
    StorageRef target = { lights.skyViewLut, 0u };
    float3 sky = GetSkyView(transmittance, multi, sampler, direction, lights.sunDirection.xyz, altitude);
    target.Store(thread.xy, float4(sky, 1.0));
}
