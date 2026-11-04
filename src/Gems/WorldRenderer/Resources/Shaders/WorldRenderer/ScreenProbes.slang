// The screen probes: one placed on the depth in each tile of pixels, each tracing a hemisphere of rays, first along
// the screen's depth, then through the field, and ending in the world probes, with the rays given to the directions
// last frame's probe found bright; each then blended with what last frame's probe at the same surface held,
// filtered over its neighbours on the same plane, and summed into the irradiance at its surface. The pass header's x is the frame's constants, y the shadow constants, z the lighting
// constants and w the field constants.
module "WorldRenderer/ScreenProbes";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Color;
import Core.Random;
import Core.Sampling;
import Scene.Instances;
import Scene.Lighting;
import Scene.Field;
import Scene.Probes;
import Scene.SurfaceCache;
import Scene.Translucency;
import Sky.ISky;
import Trace.IWorldTracer;
import Trace.DistanceField;

static const float HistoryWeight = 0.9;
// The most rays one texel of a probe traces, when its direction held most of what the probe saw last frame.
static const uint MaxRaysPerTexel = 4u;

// Each tile's probe placed at one of its pixels, a different one each frame, on the surface drawn there.
[shader("compute")]
[numthreads(8, 8, 1)]
void ScreenProbePlace(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 tile = thread.xy;
    if (tile.x >= gi.screenProbesX || tile.y >= gi.screenProbesY)
        return;

    uint2 size = uint2(frame.width, frame.height);
    float3 random = GetRandom(tile, gi.frameIndex, 0u);
    uint2 pixel = min((tile * ScreenProbeTile) + uint2(random.xy * float(ScreenProbeTile)), size - 1u);
    ScreenProbe probe;
    probe.position = float3(0.0);
    probe.viewDepth = 0.0;
    probe.normal = float3(0.0, 1.0, 0.0);
    probe.flags = 0u;
    probe.previousUv = float2(0.0);
    probe.padding = float2(0.0);
    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(pixel, 0u) != 0u)
    {
        TextureRef depthTexture = { frame.depth };
        TextureRef normalTexture = { frame.gbufferNormal };
        TextureRef velocityTexture = { frame.velocity };
        float depth = depthTexture.Load(pixel, 0u).x;
        float2 uv = GetTexelCenterUv(pixel, size);
        probe.position = ReconstructWorld(frame, uv, depth);
        probe.viewDepth = ToViewDepth(depth, frame.near);
        probe.normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
        probe.flags = ScreenProbeFlagValid;
        probe.previousUv = uv + velocityTexture.Load(pixel, 0u).xy;
    }

    ArrayRef<ScreenProbe> probes = { gi.screenProbes };
    probes[GetScreenProbeIndex(gi, tile)] = probe;
}

// Where last frame's probe at the same surface as a probe sits in the previous atlas, by where the probe's surface
// was on last frame's screen; false when there is none.
bool FindPreviousTile(FrameConstants frame, GiConstants gi, ScreenProbe probe, out uint2 previousTile)
{
    previousTile = uint2(0u);
    float2 previousUv = probe.previousUv;
    if (any(previousUv < 0.0) || any(previousUv >= 1.0))
        return false;

    previousTile = min(uint2(previousUv * float2(frame.width, frame.height)) / ScreenProbeTile, uint2(gi.screenProbesX, gi.screenProbesY) - 1u);
    ScreenProbe previous = LoadScreenProbe(gi.screenProbesPrevious, GetScreenProbeIndex(gi, previousTile));
    if ((previous.flags & ScreenProbeFlagValid) == 0u)
        return false;

    return GetProbePlaneWeight(probe.position, probe.normal, probe.viewDepth, previous.position, previous.normal) > 0.0;
}

// One ray of a probe: along the screen, then the field, then the world probes, then the sky.
float3 TraceProbeRay<S : ISky, T : IWorldTracer>(FrameConstants frame, LightingConstants lighting, GiConstants gi, FieldConstants field, ScreenProbe probe, float3 origin, float start, float3 direction)
{
    float3 radiance;
    float reach;
    if (!TraceScreenToSurface(frame, gi, probe.position + probe.normal * 0.02, direction, radiance, reach))
    {
        Ray ray = { origin, direction };
        TraceLimits limits = { start, gi.rayRange, 0.0 };
        TraceHit hit = T.Trace(ray, limits);
        reach = min(hit.distance, gi.rayRange);
        if (hit.isHit)
        {
            radiance = ShadeFieldHit(frame, field, gi, origin + direction * hit.distance, hit.normal, hit.instance);
        }
        else if (!SampleWorldProbeRadiance(gi, origin + direction * reach, direction, radiance))
        {
            SkyInputs sky;
            sky.viewDirection = direction;
            sky.sunDirection = frame.sunDirection.xyz;
            sky.withSun = false;
            sky.sunIlluminance = lighting.sunIlluminance.rgb;
            sky.altitude = lighting.cameraAltitude.x;
            sky.transmittanceLut = lighting.transmittanceLut;
            sky.multiScatteringLut = lighting.multiScatteringLut;
            sky.skyViewLut = lighting.skyViewLut;
            sky.sampler = lighting.lutSampler;
            radiance = lighting.hasSky != 0u ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r;
        }
    }

    return radiance * GetGlassTransmittance(GetTranslucency(), origin, origin + direction * reach);
}

groupshared float historyLuminance[ScreenProbeRays];

// Each probe's texels traced over the hemisphere, the rays shared out by what last frame's probe saw in each
// direction: a texel that held much of the light traces several rays, one that held almost none keeps what it held.
[shader("compute")]
[numthreads(64, 1, 1)]
void ScreenProbeTrace<S : ISky, T : IWorldTracer>(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    FieldConstants field = GetFieldConstants();
    uint2 tile = uint2(group.x % gi.screenProbesX, group.x / gi.screenProbesX);
    if (tile.y >= gi.screenProbesY)
        return;

    ScreenProbe probe = LoadScreenProbe(gi.screenProbes, group.x);
    uint2 texel = uint2(local.x % ScreenProbeTexels, local.x / ScreenProbeTexels);
    StorageRef radianceAtlas = { gi.screenRadiance, 0u };
    uint2 atlasTexel = GetScreenProbeOrigin(tile) + texel;
    if ((probe.flags & ScreenProbeFlagValid) == 0u)
    {
        radianceAtlas.Store(atlasTexel, float4(0.0));
        return;
    }

    // What the texel's direction held last frame, as its share of the probe's light.
    TextureRef previousAtlas = { gi.screenRadiancePrevious };
    uint2 previousTile;
    bool hasHistory = FindPreviousTile(frame, gi, probe, previousTile);
    float3 history = hasHistory ? previousAtlas.Load(GetScreenProbeOrigin(previousTile) + texel, 0u).rgb : float3(0.0);
    historyLuminance[local.x] = hasHistory ? GetLuminance(history) : 1.0;
    GroupMemoryBarrierWithGroupSync();
    float total = 0.0;
    for (uint i = 0u; i < ScreenProbeRays; i++)
    {
        total += historyLuminance[i];
    }

    uint rays = total > 0.0 ? min(uint(round(historyLuminance[local.x] / total * float(ScreenProbeRays))), MaxRaysPerTexel) : 1u;
    if (rays == 0u && !hasHistory)
        rays = 1u;

    float3x3 basis = CreateBasis(probe.normal);
    uint level;
    SampleField(field, probe.position, level);
    float start = field.voxelSizes[level] * 1.5;
    float3 origin = probe.position + probe.normal * start;
    float3 radiance = history;
    if (rays > 0u)
    {
        radiance = float3(0.0);
        for (uint r = 0u; r < rays; r++)
        {
            float2 jitter = GetRandom(tile, gi.frameIndex, 1u + local.x + r * ScreenProbeRays).xy;
            float3 direction = GetScreenProbeDirection(basis, texel, jitter);
            radiance += TraceProbeRay<S, T>(frame, lighting, gi, field, probe, origin, start, direction);
        }

        radiance /= float(rays);
    }

    radianceAtlas.Store(atlasTexel, float4(radiance, 1.0));
}

// Each probe's texels blended with last frame's probe at the same surface, found by where the probe was on the
// last frame's screen.
[shader("compute")]
[numthreads(8, 8, 1)]
void ScreenProbeTemporal(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 tile = thread.xy / ScreenProbeTexels;
    if (tile.x >= gi.screenProbesX || tile.y >= gi.screenProbesY)
        return;

    ScreenProbe probe = LoadScreenProbe(gi.screenProbes, GetScreenProbeIndex(gi, tile));
    if ((probe.flags & ScreenProbeFlagValid) == 0u)
        return;

    uint2 previousTile;
    if (!FindPreviousTile(frame, gi, probe, previousTile))
        return;

    ScreenProbe previous = LoadScreenProbe(gi.screenProbesPrevious, GetScreenProbeIndex(gi, previousTile));
    float weight = GetProbePlaneWeight(probe.position, probe.normal, probe.viewDepth, previous.position, previous.normal) * HistoryWeight;
    if (weight <= 0.0)
        return;

    StorageRef radianceAtlas = { gi.screenRadiance, 0u };
    TextureRef previousAtlas = { gi.screenRadiancePrevious };
    uint2 texel = thread.xy % ScreenProbeTexels;
    float3 current = radianceAtlas.Load(thread.xy).rgb;
    float3 history = previousAtlas.Load(GetScreenProbeOrigin(previousTile) + texel, 0u).rgb;
    radianceAtlas.Store(thread.xy, float4(lerp(current, history, weight), 1.0));
}

groupshared float3 filteredTexels[ScreenProbeRays];

// Each probe's texels averaged with its neighbours' on the same plane, and its irradiance summed from them.
[shader("compute")]
[numthreads(64, 1, 1)]
void ScreenProbeFilter(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 tile = uint2(group.x % gi.screenProbesX, group.x / gi.screenProbesX);
    if (tile.y >= gi.screenProbesY)
        return;

    ScreenProbe probe = LoadScreenProbe(gi.screenProbes, group.x);
    uint2 texel = uint2(local.x % ScreenProbeTexels, local.x / ScreenProbeTexels);
    TextureRef traced = { gi.screenRadiance };
    StorageRef filtered = { gi.screenFiltered, 0u };
    ArrayRef<float4> irradiance = { gi.screenIrradiance };
    float3 sum = float3(0.0);
    float weightSum = 0.0;
    if ((probe.flags & ScreenProbeFlagValid) != 0u)
    {
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                int2 otherTile = int2(tile) + int2(x, y);
                if (any(otherTile < 0) || otherTile.x >= int(gi.screenProbesX) || otherTile.y >= int(gi.screenProbesY))
                    continue;

                ScreenProbe other = LoadScreenProbe(gi.screenProbes, GetScreenProbeIndex(gi, uint2(otherTile)));
                if ((other.flags & ScreenProbeFlagValid) == 0u)
                    continue;

                float weight = (x == 0 && y == 0) ? 1.0 : GetProbePlaneWeight(probe.position, probe.normal, probe.viewDepth, other.position, other.normal);
                sum += traced.Load(GetScreenProbeOrigin(uint2(otherTile)) + texel, 0u).rgb * weight;
                weightSum += weight;
            }
        }
    }

    float3 value = sum / max(weightSum, Epsilon);
    filtered.Store(GetScreenProbeOrigin(tile) + texel, float4(value, 1.0));
    filteredTexels[local.x] = value;
    GroupMemoryBarrierWithGroupSync();
    if (local.x != 0u)
        return;

    float3 total = float3(0.0);
    for (uint i = 0u; i < ScreenProbeRays; i++)
    {
        total += filteredTexels[i];
    }

    irradiance[group.x] = float4(total * (Pi / float(ScreenProbeRays)), (probe.flags & ScreenProbeFlagValid) != 0u ? 1.0 : 0.0);
}
