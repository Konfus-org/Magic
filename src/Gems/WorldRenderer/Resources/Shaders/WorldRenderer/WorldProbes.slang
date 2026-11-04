// The world probes kept up to date: each frame the probes whose cells moved with the windows, and those an
// instance changed under, are marked new, a budget of probes is picked, the new ones and a share of the rest in
// turn, and each picked probe traces its rays through the tracer the entry is specialised to, shades what they land
// on from the surface cache and what they miss from the sky, folds them into its spheres of radiance and distance,
// and convolves its irradiance. The pass header's x is the frame's constants, y the shadow constants, z the
// lighting constants and w the field constants.
module "WorldRenderer/WorldProbes";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Scene.Lighting;
import Scene.Field;
import Scene.Probes;
import Scene.SurfaceCache;
import Scene.Translucency;
import Shadow.Virtual;
import Sky.ISky;
import Trace.IWorldTracer;
import Trace.DistanceField;

// Frames between a probe's updates when nothing marks it new.
static const uint ProbePeriod = 128u;
// What a probe keeps of what it held when it updates: it updates seldom, so not much.
static const float ProbeHysteresis = 0.6;

// Each cell whose window moved on takes the probe now at it and is marked new; the list of probes to update is
// emptied.
[shader("compute")]
[numthreads(64, 1, 1)]
void WorldProbeScroll(uint3 thread : SV_DispatchThreadID)
{
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint cell = thread.x;
    if (cell == 0u)
    {
        BufferRef list = { gi.probeList };
        list.Store<uint>(0u, 0u);
    }

    if (cell >= ProbeLevels * ProbesPerLevel)
        return;

    uint level = cell / ProbesPerLevel;
    uint local = cell % ProbesPerLevel;
    int3 localProbe = int3(local % ProbesPerAxis, (local / ProbesPerAxis) % ProbesPerAxis, local / (ProbesPerAxis * ProbesPerAxis));
    int3 origin = GetProbeWindowOrigin(gi, level);
    int3 world = origin + ((localProbe - origin) & int(ProbesPerAxis - 1u));
    ArrayRef<ProbeState> states = { gi.probeStates };
    ProbeState state = states[cell];
    if (all(state.world == world) && state.flags != 0u)
        return;

    state.world = world;
    state.flags = ProbeFlagNew;
    states[cell] = state;
}

// Each new probe, each probe an instance changed under this frame, which starts afresh, and each probe whose turn
// it is, joins the list up to the budget.
[shader("compute")]
[numthreads(64, 1, 1)]
void WorldProbeSelect(uint3 thread : SV_DispatchThreadID)
{
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    FieldConstants field = GetFieldConstants();
    uint cell = thread.x;
    if (cell >= ProbeLevels * ProbesPerLevel)
        return;

    ArrayRef<ProbeState> states = { gi.probeStates };
    ProbeState state = states[cell];
    uint level = cell / ProbesPerLevel;
    float3 position = GetProbePosition(gi, level, state.world);
    float reach = gi.probeSpacings[level];
    ArrayRef<DirtyInstance> dirty = { field.dirtyInstances };
    for (uint i = 0u; i < field.dirtyInstanceCount; i++)
    {
        DirtyInstance instance = dirty[i];
        bool wasUnder = instance.oldRadius > 0.0 && distance(position, instance.oldCenter) < instance.oldRadius + reach;
        bool isUnder = instance.newRadius > 0.0 && distance(position, instance.newCenter) < instance.newRadius + reach;
        if (wasUnder || isUnder)
        {
            state.flags = ProbeFlagNew;
            states[cell] = state;
            break;
        }
    }

    bool isNew = (state.flags & ProbeFlagNew) != 0u;
    bool isDue = ((cell * 7u) + gi.frameIndex) % ProbePeriod == 0u;
    if (!isNew && !isDue)
        return;

    BufferRef list = { gi.probeList };
    uint slot = list.InterlockedAdd(0u, 1u);
    if (slot < gi.probeBudget)
        list.Store<uint>((1u + slot) * 4u, cell);
}

groupshared float3 rayRadiance[ProbeRays];
groupshared float rayDistance[ProbeRays];
groupshared float3 texelRadiance[ProbeTexels * ProbeTexels];

// A direction of the spherical Fibonacci set, turned by the frame so the set drifts between updates.
float3 GetProbeRay(uint i, uint frame, uint cell)
{
    float golden = 2.399963229728653;
    float z = 1.0 - (2.0 * float(i) + 1.0) / float(ProbeRays);
    float radius = sqrt(max(0.0, 1.0 - z * z));
    float turn = ToUnitFloat(GetHash(frame * 2654435761u + cell)) * TwoPi;
    float angle = float(i) * golden + turn;
    float3 direction = float3(radius * cos(angle), z, radius * sin(angle));
    return (frame & 1u) != 0u ? direction.zyx : direction;
}

// Each listed probe traces its rays, folds them into its spheres and convolves its irradiance.
[shader("compute")]
[numthreads(256, 1, 1)]
void WorldProbeTrace<S : ISky, T : IWorldTracer>(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    FieldConstants field = GetFieldConstants();
    BufferRef list = { gi.probeList };
    if (group.x >= min(list.Load<uint>(0u), gi.probeBudget))
        return;

    uint cell = list.Load<uint>((1u + group.x) * 4u);
    uint level = cell / ProbesPerLevel;
    ProbeState state = LoadProbeState(gi, cell);
    float3 position = GetProbePosition(gi, level, state.world);
    bool isNew = (state.flags & ProbeFlagValid) == 0u;

    SkyInputs sky;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.hasSunDisc = false;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;
    bool hasSky = lighting.hasSky != 0u;

    // A probe inside a surface sees nothing: black at no distance, so nothing reads it.
    uint unused;
    bool isInside = SampleField(field, position, unused) < 0.0;
    uint ray = local.x;
    float3 direction = GetProbeRay(ray, gi.frameIndex, cell);
    float3 radiance = float3(0.0);
    float hitDistance = 0.0;
    if (!isInside)
    {
        Ray traced = { position, direction };
        TraceLimits limits = { 0.0, gi.rayRange, 0.0 };
        TraceHit hit = T.Trace(traced, limits);
        if (hit.isHit)
        {
            radiance = ShadeFieldHit(frame, field, gi, position + direction * hit.distance, hit.normal, hit.instance);
            hitDistance = hit.distance;
        }
        else
        {
            sky.viewDirection = direction;
            radiance = hasSky ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r;
            hitDistance = gi.rayRange;
        }
    }

    rayRadiance[ray] = radiance * GetGlassTransmittance(GetTranslucency(), position, position + direction * hitDistance);
    rayDistance[ray] = hitDistance;
    GroupMemoryBarrierWithGroupSync();

    // Each texel of the spheres: the rays near its direction, the nearest weighing most.
    uint texelIndex = local.x;
    uint2 texel = uint2(texelIndex % ProbeTexels, texelIndex / ProbeTexels);
    float3 texelDirection = GetOctahedralDirection(texel, ProbeTexels);
    float3 radianceSum = float3(0.0);
    float radianceWeight = 0.0;
    float2 distanceSum = float2(0.0);
    float distanceWeight = 0.0;
    for (uint i = 0u; i < ProbeRays; i++)
    {
        float cosine = max(dot(texelDirection, GetProbeRay(i, gi.frameIndex, cell)), 0.0);
        float narrow = Pow4(Pow4(cosine));
        radianceSum += rayRadiance[i] * narrow;
        radianceWeight += narrow;
        float narrower = narrow * narrow * narrow;
        distanceSum += float2(rayDistance[i], rayDistance[i] * rayDistance[i]) * narrower;
        distanceWeight += narrower;
    }

    float3 newRadiance = radianceSum / max(radianceWeight, Epsilon);
    float2 newDistance = distanceSum / max(distanceWeight, Epsilon);
    uint2 atlasTexel = GetProbeAtlasOrigin(cell) + texel;
    StorageRef radianceAtlas = { gi.probeRadiance, 0u };
    StorageRef distanceAtlas = { gi.probeDistance, 0u };
    float hysteresis = isNew ? 0.0 : ProbeHysteresis;
    float3 blended = lerp(newRadiance, radianceAtlas.Load(atlasTexel).rgb, hysteresis);
    float2 blendedDistance = lerp(newDistance, distanceAtlas.Load(atlasTexel).xy, hysteresis);
    radianceAtlas.Store(atlasTexel, float4(blended, 1.0));
    distanceAtlas.Store(atlasTexel, float4(blendedDistance, 0.0, 0.0));
    texelRadiance[texelIndex] = blended;
    GroupMemoryBarrierWithGroupSync();

    // The irradiance sphere from the radiance sphere: every texel's radiance under the cosine, over its solid angle.
    if (local.x < ProbeIrradianceTexels * ProbeIrradianceTexels)
    {
        uint2 irradianceTexel = uint2(local.x % ProbeIrradianceTexels, local.x / ProbeIrradianceTexels);
        float3 normal = GetOctahedralDirection(irradianceTexel, ProbeIrradianceTexels);
        float3 irradiance = float3(0.0);
        for (uint i = 0u; i < ProbeTexels * ProbeTexels; i++)
        {
            float3 sampleDirection = GetOctahedralDirection(uint2(i % ProbeTexels, i / ProbeTexels), ProbeTexels);
            irradiance += texelRadiance[i] * max(dot(normal, sampleDirection), 0.0);
        }

        StorageRef irradianceAtlas = { gi.probeIrradiance, 0u };
        irradianceAtlas.Store(GetProbeIrradianceOrigin(cell) + irradianceTexel, float4(irradiance * (2.0 * TwoPi / float(ProbeTexels * ProbeTexels)), 1.0));
    }

    if (local.x == 0u)
    {
        ArrayRef<ProbeState> states = { gi.probeStates };
        state.flags = ProbeFlagValid;
        states[cell] = state;
    }
}
