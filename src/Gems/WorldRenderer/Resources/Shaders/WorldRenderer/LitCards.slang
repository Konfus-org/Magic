// The lit cards: a batch of the slots each frame, the lights that reach each slot's instance listed, then every
// texel of the slot's capture lit where the capture holds a surface: the sun through its pages, the listed lights
// and the world probes' irradiance, all diffuse, what a ray reads when it lands on the instance. The pass header's
// x is the frame's constants, y the shadow constants, z the lighting constants and w the field constants.
module "WorldRenderer/LitCards";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Instances;
import Scene.Captures;
import Scene.Lights;
import Scene.Lighting;
import Scene.Probes;
import Scene.SurfaceCache;
import Light.Lights;
import Shadow.Virtual;
import Sky.ISky;

// The slot a group of the batch lights.
uint GetBatchSlot(GiConstants gi, uint group)
{
    ArrayRef<uint> batch = { gi.slotBatch };
    return batch[group];
}

// Each lit slot of the batch lists the lights whose reach touches its instance's sphere.
[shader("compute")]
[numthreads(64, 1, 1)]
void LitCardLights(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint slot = GetBatchSlot(gi, group.x);
    LitSlot litSlot = LoadLitSlot(gi, slot);
    GpuInstance instance = LoadInstance(frame.instances, litSlot.instance);
    BufferRef lists = { gi.slotLights };
    uint listOffset = slot * (LitLightsPerSlot + 1u) * 4u;
    if (local.x == 0u)
        lists.Store<uint>(listOffset, 0u);

    GroupMemoryBarrierWithGroupSync();
    for (uint i = local.x; i < lighting.lightCount; i += 64u)
    {
        GpuLight light = LoadLight(lighting.lights, i);
        if (distance(light.position, instance.boundsCenter) > light.range + instance.boundsRadius)
            continue;

        uint count = lists.InterlockedAdd(listOffset, 1u);
        if (count < LitLightsPerSlot)
            lists.Store<uint>(listOffset + (1u + count) * 4u, i);
    }
}

// Each texel of each slot of the batch lit, from the capture's albedo, normal and depth at twice its place.
[shader("compute")]
[numthreads(8, 8, 1)]
void LitCardShade<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint slot = GetBatchSlot(gi, thread.z);
    LitSlot litSlot = LoadLitSlot(gi, slot);
    GpuInstance instance = LoadInstance(frame.instances, litSlot.instance);
    GpuCapture capture = LoadCapture(frame.captures, litSlot.capture);
    StorageRef pool = { gi.litPool, 0u };
    uint2 poolTexel = GetLitSlotOrigin(slot) + thread.xy;

    uint2 cell = thread.xy / LitCellTexels;
    float2 cellUv = (float2(thread.xy % LitCellTexels) + 0.5) / float(LitCellTexels);
    float2 captureUv = (float2(cell) + cellUv) / float(CaptureCells);
    SamplerRef sampler = { frame.captureSampler };
    TextureRef albedoAtlas = { capture.albedo };
    TextureRef normalAtlas = { capture.normal };
    TextureRef depthAtlas = { capture.depth };
    float4 albedo = albedoAtlas.SampleLevel(sampler, captureUv, 0.0);
    uint2 captureTexel = (cell * CaptureCellSize) + uint2(cellUv * float(CaptureCellSize));
    float depth = depthAtlas.Load(captureTexel, 0u).x;
    if (albedo.a < 0.5 || depth <= 0.0)
    {
        pool.Store(poolTexel, float4(0.0));
        return;
    }

    // The model point the cell's view put at this texel, and its surface's normal, both into the world.
    float3 direction = GetCaptureDirection(cell);
    float3 right;
    float3 up;
    float3 forward;
    GetCaptureBasis(direction, right, up, forward);
    float radius = max(capture.boundsRadius, 1e-3);
    float3 eye = capture.boundsCenter + direction * 2.0 * radius;
    float3 model = eye + right * ((cellUv.x * 2.0 - 1.0) * radius) + up * ((1.0 - cellUv.y * 2.0) * radius) + forward * ((1.5 - depth) * 2.0 * radius);
    float3 modelNormal = DecodeOctahedral(normalAtlas.Load(captureTexel, 0u).xy * 2.0 - 1.0);
    float3 world = mul(float4(model, 1.0), instance.world).xyz;
    float3 normal = ToWorldNormal(instance.world, modelNormal);

    SkyInputs sky;
    sky.viewDirection = normal;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;
    bool hasSky = lighting.hasSky != 0u;

    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    float3 sunIlluminance = hasSky ? S.GetSunIlluminance(sky) : lighting.sunIlluminance.rgb;
    float viewDepth = max(distance(world, frame.cameraPosition.xyz), frame.near);
    float visible = SampleSunShadow(shadow, world, normal, viewDepth);
    float3 irradiance = sunIlluminance * (visible * saturate(dot(normal, frame.sunDirection.xyz)));

    BufferRef lists = { gi.slotLights };
    uint listOffset = slot * (LitLightsPerSlot + 1u) * 4u;
    uint count = min(lists.Load<uint>(listOffset), LitLightsPerSlot);
    for (uint i = 0u; i < count; i++)
    {
        GpuLight light = LoadLight(lighting.lights, lists.Load<uint>(listOffset + (1u + i) * 4u));
        LightSample sample = SampleLight(light, world, normal);
        irradiance += sample.irradiance * saturate(dot(normal, sample.direction));
    }

    float3 indirect;
    if (!SampleWorldProbeIrradiance(gi, world + normal * 0.1, normal, indirect))
        indirect = hasSky ? S.GetAmbient(sky, normal) : lighting.sunIlluminance.rgb * 0.1 * (0.5 + 0.5 * normal.y);

    irradiance += indirect;
    pool.Store(poolTexel, float4(albedo.rgb * InvPi * irradiance, 1.0));
}
