// The atmosphere's three tables, made ahead of each frame: transmittance through the air by height and direction,
// the light every direction scatters again by height and sun, and the sky as seen from the camera's height. The
// pass header's x is the frame's constants, y the shadow constants and z the lighting constants.
module "WorldRenderer/SkyLuts";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lighting;
import Sky.Atmosphere;

[shader("compute")]
[numthreads(8, 8, 1)]
void SkyTransmittance(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= TransmittanceLutSize))
        return;

    LightingConstants lighting = GetLighting();
    float r;
    float mu;
    TransmittanceParams(GetTexelCenterUv(thread.xy, TransmittanceLutSize), r, mu);
    StorageRef target = { lighting.transmittanceLut, 0u };
    target.Store(thread.xy, float4(ComputeTransmittance(r, mu), 1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void SkyMultiScattering(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= MultiScatteringLutSize))
        return;

    LightingConstants lighting = GetLighting();
    TextureRef transmittance = { lighting.transmittanceLut };
    SamplerRef sampler = { lighting.lutSampler };
    StorageRef target = { lighting.multiScatteringLut, 0u };
    float3 multi = ComputeMultiScattering(transmittance, sampler, GetTexelCenterUv(thread.xy, MultiScatteringLutSize));
    target.Store(thread.xy, float4(multi, 1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void SkyView(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= SkyViewLutSize))
        return;

    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    TextureRef transmittance = { lighting.transmittanceLut };
    TextureRef multi = { lighting.multiScatteringLut };
    SamplerRef sampler = { lighting.lutSampler };
    float altitude = lighting.cameraAltitude.x * 0.001;
    float3 direction = SkyViewDirection(GetTexelCenterUv(thread.xy, SkyViewLutSize), altitude);
    StorageRef target = { lighting.skyViewLut, 0u };
    float3 sky = ComputeSkyView(transmittance, multi, sampler, direction, frame.sunDirection.xyz, altitude);
    target.Store(thread.xy, float4(sky, 1.0));
}
