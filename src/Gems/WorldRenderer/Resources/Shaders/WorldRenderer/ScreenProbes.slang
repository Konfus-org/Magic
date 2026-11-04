// The screen probes: one placed on the depth in each tile of pixels, each tracing a hemisphere of rays, first along
// the screen's depth, then through the field, and ending in the world probes; each then blended with what last
// frame's probe at the same surface held, filtered over its neighbours on the same plane, and summed into the
// irradiance at its surface. The pass header's x is the frame's constants, y the shadow constants, z the lighting
// constants and w the field constants.
module "WorldRenderer/ScreenProbes";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Instances;
import Scene.Lighting;
import Scene.Field;
import Scene.Probes;
import Scene.SurfaceCache;
import Sky.ISky;
import Trace.IWorldTracer;
import Trace.DistanceField;

static const float HistoryWeight = 0.85;

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
    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(pixel, 0u) != 0u)
    {
        TextureRef depthTexture = { frame.depth };
        TextureRef normalTexture = { frame.gbufferNormal };
        float depth = depthTexture.Load(pixel, 0u).x;
        probe.position = ReconstructWorld(frame, GetTexelCenterUv(pixel, size), depth);
        probe.viewDepth = ToViewDepth(depth, frame.near);
        probe.normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
        probe.flags = ScreenProbeFlagValid;
    }

    ArrayRef<ScreenProbe> probes = { gi.screenProbes };
    probes[GetScreenProbeIndex(gi, tile)] = probe;
}

// Each probe's texels traced: a ray each over the hemisphere, the screen, then the field, then the world probes.
[shader("compute")]
[numthreads(64, 1, 1)]
void ScreenProbeTrace<S : ISky>(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
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

    float3x3 basis = CreateBasis(probe.normal);
    float2 jitter = GetRandom(tile, gi.frameIndex, 1u + local.x).xy;
    float3 direction = GetScreenProbeDirection(basis, texel, jitter);
    uint level;
    SampleField(field, probe.position, level);
    float start = field.voxelSizes[level] * 1.5;
    float3 origin = probe.position + probe.normal * start;

    float3 radiance;
    float screenDistance;
    if (!TraceScreenToSurface(frame, gi, probe.position + probe.normal * 0.02, direction, radiance, screenDistance))
    {
        Ray ray = { origin, direction };
        TraceLimits limits = { start, gi.rayRange, 0.0 };
        TraceHit hit = DistanceFieldTracer.Trace(ray, limits);
        if (hit.isHit)
        {
            radiance = ShadeFieldHit(frame, field, gi, origin + direction * hit.distance, hit.normal, direction);
        }
        else if (!SampleWorldProbeRadiance(gi, origin + direction * min(hit.distance, gi.rayRange), direction, radiance))
        {
            SkyInputs sky;
            sky.viewDirection = direction;
            sky.sunDirection = frame.sunDirection.xyz;
            sky.sunIlluminance = lighting.sunIlluminance.rgb;
            sky.altitude = lighting.cameraAltitude.x;
            sky.transmittanceLut = lighting.transmittanceLut;
            sky.multiScatteringLut = lighting.multiScatteringLut;
            sky.skyViewLut = lighting.skyViewLut;
            sky.sampler = lighting.lutSampler;
            radiance = lighting.hasSky != 0u ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r;
        }
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

    float4 previousClip = mul(float4(probe.position - frame.previousCameraPosition.xyz, 1.0), frame.previousViewProjection);
    if (previousClip.w <= frame.near)
        return;

    float2 previousUv = float2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
    if (any(previousUv < 0.0) || any(previousUv >= 1.0))
        return;

    uint2 previousTile = min(uint2(previousUv * float2(frame.width, frame.height)) / ScreenProbeTile, uint2(gi.screenProbesX, gi.screenProbesY) - 1u);
    ScreenProbe previous = LoadScreenProbe(gi.screenProbesPrevious, GetScreenProbeIndex(gi, previousTile));
    if ((previous.flags & ScreenProbeFlagValid) == 0u)
        return;

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
