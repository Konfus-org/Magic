// The lit cards: a batch of the slots each frame, the lights that reach each slot's instance listed, the radiosity
// of each tile of each card traced, then every lit texel of the slot's cards given the irradiance on the surface
// there: the sun through its pages, the listed lights and the tile's radiosity, what a ray reads when it lands on
// the instance, under the card's albedo. The pass header's x is the frame's constants, y the shadow constants, z the
// lighting constants and w the field constants.
module "WorldRenderer/LitCards";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Instances;
import Scene.Cards;
import Scene.Lights;
import Scene.Lighting;
import Scene.Field;
import Scene.Probes;
import Scene.SurfaceCache;
import Scene.Translucency;
import Light.Lights;
import Shadow.Virtual;
import Sky.ISky;
import Trace.IWorldTracer;
import Trace.DistanceField;

// Rays a tile's radiosity traces each time its slot is lit, and what it keeps of what it held.
static const uint RadiosityRays = 16u;
static const float RadiosityHysteresis = 0.75;

// The slot a group of the batch lights.
uint GetBatchSlot(GiConstants gi, uint group)
{
    ArrayRef<uint> batch = { gi.slotBatch };
    return batch[group];
}

// The surface a card's texel holds, in the world: false where the capture saw nothing there.
bool LoadCardSurface(FrameConstants frame, GpuInstance instance, GpuCard card, float2 uv, out float3 world, out float3 normal)
{
    world = float3(0.0);
    normal = float3(0.0, 1.0, 0.0);
    uint2 size = GetCardSize(card);
    uint2 texel = GetCardOrigin(card) + min(uint2(uv * float2(size)), size - 1u);
    TextureRef albedoAtlas = { frame.cardAlbedo };
    TextureRef normalAtlas = { frame.cardNormal };
    TextureRef depthAtlas = { frame.cardDepth };
    float depth = depthAtlas.Load(texel, 0u).x;
    if (albedoAtlas.Load(texel, 0u).a < 0.5 || depth <= 0.0)
        return false;

    float3 modelNormal = DecodeOctahedral(normalAtlas.Load(texel, 0u).xy * 2.0 - 1.0);
    world = mul(float4(UnprojectFromCard(card, uv, depth), 1.0), instance.world).xyz;
    normal = ToWorldNormal(instance.world, modelNormal);
    return true;
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
    GpuLitSlot litSlot = LoadLitSlot(gi, slot);
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

groupshared float3 radiosityRays[RadiosityRays];

// Each tile of each card of the batch: rays over the hemisphere of the tile's surface, through the field, read from
// the lit cards they land on, the world probes past them and the sky, summed into the irradiance the tile receives
// and blended with what it held. The group's xy is the tile and its z the batch's card.
[shader("compute")]
[numthreads(16, 1, 1)]
void LitCardRadiosity<S : ISky, T : IWorldTracer>(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    FieldConstants field = GetFieldConstants();
    uint slot = GetBatchSlot(gi, group.z / CardsPerCapture);
    uint side = group.z % CardsPerCapture;
    GpuLitSlot litSlot = LoadLitSlot(gi, slot);
    GpuInstance instance = LoadInstance(frame.instances, litSlot.instance);
    GpuCard card = LoadCard(frame.cards, (litSlot.capture * CardsPerCapture) + side);
    uint2 litSize = GetLitCardSize(card);
    if (any(group.xy * LitTileTexels >= litSize))
        return;

    StorageRef radiosity = { gi.cardRadiosity, 0u };
    uint2 tileTexel = (GetLitCardOrigin(slot, side) / LitTileTexels) + group.xy;
    float2 uv = min((float2(group.xy) + 0.5) * float(LitTileTexels), float2(litSize) - 0.5) / float2(litSize);
    float3 world;
    float3 normal;
    if (!LoadCardSurface(frame, instance, card, uv, world, normal))
    {
        if (local.x == 0u)
            radiosity.Store(tileTexel, float4(0.0));

        return;
    }

    SkyInputs sky;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.hasSunDisc = false;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;

    float3x3 basis = CreateBasis(normal);
    float2 xi = GetRandom(tileTexel, gi.frameIndex, local.x).xy;
    float pdf;
    float3 direction = ToWorld(basis, SampleCosineHemisphere(xi, pdf));
    uint level;
    SampleField(field, world, level);
    float start = field.voxelSizes[level] * 1.5;
    float3 origin = world + normal * start;
    Ray ray = { origin, direction };
    TraceLimits limits = { start, gi.rayRange, 0.0 };
    TraceHit hit = T.Trace(ray, limits);
    float3 radiance;
    if (hit.isHit)
    {
        radiance = ShadeFieldHit(frame, field, gi, origin + direction * hit.distance, hit.normal, hit.instance);
    }
    else if (!SampleWorldProbeRadiance(gi, origin + direction * min(hit.distance, gi.rayRange), direction, radiance))
    {
        sky.viewDirection = direction;
        radiance = lighting.hasSky != 0u ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r;
    }

    radiosityRays[local.x] = radiance * GetGlassTransmittance(GetTranslucency(), origin, origin + direction * min(hit.distance, gi.rayRange));
    GroupMemoryBarrierWithGroupSync();
    if (local.x != 0u)
        return;

    float3 sum = float3(0.0);
    for (uint i = 0u; i < RadiosityRays; i++)
    {
        sum += radiosityRays[i];
    }

    // Cosine-distributed rays: their mean times pi is the irradiance.
    float3 irradiance = sum * (Pi / float(RadiosityRays));
    if (litSlot.frames != 0u)
        irradiance = lerp(irradiance, radiosity.Load(tileTexel).rgb, RadiosityHysteresis);

    radiosity.Store(tileTexel, float4(irradiance, 1.0));
}

// Each lit texel of each card of the batch: the irradiance on the surface the card holds there, from the sun, the
// slot's lights and the tile radiosity around it. The group's z is the batch's card.
[shader("compute")]
[numthreads(8, 8, 1)]
void LitCardShade<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint slot = GetBatchSlot(gi, thread.z / CardsPerCapture);
    uint side = thread.z % CardsPerCapture;
    GpuLitSlot litSlot = LoadLitSlot(gi, slot);
    GpuInstance instance = LoadInstance(frame.instances, litSlot.instance);
    GpuCard card = LoadCard(frame.cards, (litSlot.capture * CardsPerCapture) + side);
    uint2 litSize = GetLitCardSize(card);
    if (any(thread.xy >= litSize))
        return;

    StorageRef pool = { gi.litPool, 0u };
    uint2 litOrigin = GetLitCardOrigin(slot, side);
    uint2 poolTexel = litOrigin + thread.xy;
    float2 uv = (float2(thread.xy) + 0.5) / float2(litSize);
    float3 world;
    float3 normal;
    if (!LoadCardSurface(frame, instance, card, uv, world, normal))
    {
        pool.Store(poolTexel, float4(0.0));
        return;
    }

    SkyInputs sky;
    sky.viewDirection = normal;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.hasSunDisc = false;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;

    // The texel's surface is where the card saw it to within a texel, so the sun is tested a texel out along the
    // normal, where the surface's own curve cannot shadow it.
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    float3 sunIlluminance = lighting.hasSky != 0u ? S.GetSunIlluminance(sky) : lighting.sunIlluminance.rgb;
    float3 lifted = world + normal * (GetCardTexelWorld(card) * GetMaxScale(instance.world));
    float viewDepth = max(distance(lifted, frame.cameraPosition.xyz), frame.near);
    float3 sunVisible = SampleSunShadow(shadow, lifted, normal, viewDepth) * GetGlassShadow(GetTranslucency(), lifted);
    float3 irradiance = sunIlluminance * sunVisible * saturate(dot(normal, frame.sunDirection.xyz));

    BufferRef lists = { gi.slotLights };
    uint listOffset = slot * (LitLightsPerSlot + 1u) * 4u;
    uint count = min(lists.Load<uint>(listOffset), LitLightsPerSlot);
    for (uint i = 0u; i < count; i++)
    {
        GpuLight light = LoadLight(lighting.lights, lists.Load<uint>(listOffset + (1u + i) * 4u));
        LightSample sample = SampleLight(light, world, normal);
        irradiance += sample.irradiance * saturate(dot(normal, sample.direction));
    }

    // The tiles around the texel blended, kept within the card's own tiles.
    TextureRef radiosity = { gi.cardRadiosity };
    SamplerRef sampler = { frame.linearSampler };
    float2 tiles = ceil(float2(litSize) / float(LitTileTexels));
    float2 tile = clamp((float2(thread.xy) + 0.5) / float(LitTileTexels), float2(0.5), tiles - 0.5);
    float2 tileUv = (float2(litOrigin / LitTileTexels) + tile) / float(LitRadiosityTexels);
    irradiance += radiosity.SampleLevel(sampler, tileUv, 0.0).rgb;

    pool.Store(poolTexel, float4(irradiance, 1.0));
}
