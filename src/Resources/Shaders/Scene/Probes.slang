// The global illumination's tables, C#'s GiConstants: the lit cards, the world probes around the camera, the
// screen probes over the view and the reflections, and how a direction maps to a probe's texels. A world probe keeps the sphere as an
// octahedral map; a screen probe keeps the hemisphere above its surface as a square of cosine-distributed
// directions, so the average of its texels times pi is the irradiance there. The constants' heap id is the
// lighting constants' gi.
module "Scene/Probes";

import Core.Heap;
import Core.Math;
import Core.Pack;
import Core.Sampling;

// The lit cards: a pool of slots, a slot an instance's capture lit, each cell of the capture at half the texels.
public static const uint LitSlotTexels = 256u;
public static const uint LitCellTexels = 32u;
public static const uint LitPoolTexels = 4096u;
public static const uint LitSlotsPerRow = LitPoolTexels / LitSlotTexels;
public static const uint LitSlots = LitSlotsPerRow * LitSlotsPerRow;
public static const uint LitLightsPerSlot = 64u;

// The world probes: a clipmap of levels of probes around the camera, each level's probes twice as far apart as the
// last's, kept as a sphere of radiance, a sphere of distances and a sphere of irradiance.
public static const uint ProbeLevels = 4u;
public static const uint ProbesPerAxis = 16u;
public static const uint ProbesPerLevel = ProbesPerAxis * ProbesPerAxis * ProbesPerAxis;
public static const uint ProbeTexels = 16u;
public static const uint ProbeIrradianceTexels = 8u;
public static const uint ProbeRays = 256u;
public static const uint ProbesPerAtlasRow = 64u;
public static const uint ProbeFlagValid = 1u;
public static const uint ProbeFlagNew = 2u;

// The screen probes: one a tile of pixels, a hemisphere of texels each.
public static const uint ScreenProbeTile = 16u;
public static const uint ScreenProbeTexels = 8u;
public static const uint ScreenProbeRays = ScreenProbeTexels * ScreenProbeTexels;
public static const uint ScreenProbeFlagValid = 1u;

public struct GiConstants
{
    // Each level's window: the probe, counted in that level's probes, at its corner, in xyz.
    public int4 probeOrigins[ProbeLevels];
    // Metres between a level's probes.
    public float4 probeSpacings;
    // What a surface with no lit card reflects per unit of albedo, in xyz.
    public float4 fallbackIrradiance;
    // Heap ids.
    public uint litPool;
    public uint litSlots;
    public uint slotInstances;
    public uint slotLights;
    public uint probeStates;
    public uint probeList;
    public uint probeRadiance;
    public uint probeDistance;
    public uint probeIrradiance;
    public uint padding2;
    public uint screenRadiance;
    public uint screenRadiancePrevious;
    public uint screenProbes;
    public uint screenProbesPrevious;
    public uint screenIrradiance;
    public uint screenProbesX;
    public uint screenProbesY;
    public uint litSlotCount;
    public uint slotBatch;
    public uint slotsPerFrame;
    public uint probeBudget;
    public uint frameIndex;
    // Metres a probe's or a screen probe's ray goes at most.
    public float rayRange;
    public uint screenFiltered;
    public uint reflectionTraced;
    public uint reflectionResolved;
    public uint reflections;
    public uint reflectionsPrevious;
    public uint padding0;
    public uint padding1;
}

// The screen probes and the reflections: the roughness a surface reflects sharply below, its own ray a pixel, and
// the roughness from which the screen probes' radiance stands in.
public static const float ReflectionRoughnessSharp = 0.4;
public static const float ReflectionRoughnessLimit = 0.6;

// A lit card's slot: the instance it lights and that instance's capture row.
public struct LitSlot
{
    public uint instance;
    public uint capture;
    public uint padding0;
    public uint padding1;
}

// A world probe's cell: the probe, counted in its level's probes, it holds, and its ProbeFlag bits.
public struct ProbeState
{
    public int3 world;
    public uint flags;
}

// A screen probe: where it sits, its surface's normal and its ScreenProbeFlag bits.
public struct ScreenProbe
{
    public float3 position;
    public float viewDepth;
    public float3 normal;
    public uint flags;
}

// --- Lit cards ---

// The lit slot of an instance plus one; 0 while it has none.
public uint GetLitSlot(GiConstants gi, uint instanceIndex)
{
    ArrayRef<uint> slots = { gi.litSlots };
    return slots[instanceIndex];
}

public LitSlot LoadLitSlot(GiConstants gi, uint slot)
{
    ArrayRef<LitSlot> slots = { gi.slotInstances };
    return slots[slot];
}

// The texel at the top left of a slot in the pool.
public uint2 GetLitSlotOrigin(uint slot) { return uint2(slot % LitSlotsPerRow, slot / LitSlotsPerRow) * LitSlotTexels; }

// --- World probes ---

public ProbeState LoadProbeState(GiConstants gi, uint cell)
{
    ArrayRef<ProbeState> states = { gi.probeStates };
    return states[cell];
}

// The probe, counted in the level's probes, at a level's window corner.
public int3 GetProbeWindowOrigin(GiConstants gi, uint level) { return gi.probeOrigins[level].xyz; }

// A probe's cell in its level's table: the window wraps, so a probe keeps its cell as the window slides.
public uint GetProbeCell(uint level, int3 probe)
{
    int3 cell = probe & int(ProbesPerAxis - 1u);
    return (level * ProbesPerLevel) + (uint(cell.z) * ProbesPerAxis + uint(cell.y)) * ProbesPerAxis + uint(cell.x);
}

public float3 GetProbePosition(GiConstants gi, uint level, int3 probe) { return float3(probe) * gi.probeSpacings[level]; }

// Whether a level's window holds a probe, a margin of probes inside its edge.
public bool IsInProbeWindow(GiConstants gi, uint level, int3 probe, int margin)
{
    int3 origin = GetProbeWindowOrigin(gi, level);
    return all(probe >= origin + margin) && all(probe < origin + int(ProbesPerAxis) - margin);
}

// The texel at the top left of a probe's cell in the radiance and distance atlases.
public uint2 GetProbeAtlasOrigin(uint cell) { return uint2(cell % ProbesPerAtlasRow, cell / ProbesPerAtlasRow) * ProbeTexels; }

// The texel at the top left of a probe's cell in the irradiance atlas.
public uint2 GetProbeIrradianceOrigin(uint cell)
{
    return uint2(cell % ProbesPerAtlasRow, cell / ProbesPerAtlasRow) * ProbeIrradianceTexels;
}

// A texel of an octahedral map folded back in when it lies past an edge.
public int2 WrapOctahedral(int2 texel, int size)
{
    if (texel.x < 0 || texel.x >= size)
    {
        texel.x = texel.x < 0 ? -1 - texel.x : (2 * size) - 1 - texel.x;
        texel.y = size - 1 - texel.y;
    }

    if (texel.y < 0 || texel.y >= size)
    {
        texel.y = texel.y < 0 ? -1 - texel.y : (2 * size) - 1 - texel.y;
        texel.x = size - 1 - texel.x;
    }

    return texel;
}

// The direction at the centre of a texel of an octahedral map of a size.
public float3 GetOctahedralDirection(uint2 texel, uint size)
{
    float2 flat = ((float2(texel) + 0.5) / float(size)) * 2.0 - 1.0;
    return DecodeOctahedral(flat);
}

// An octahedral map of a size at a direction, the four texels around it blended, folded at the edges.
public float4 SampleOctahedral(TextureRef atlas, uint2 origin, uint size, float3 direction)
{
    float2 flat = (EncodeOctahedral(direction) * 0.5 + 0.5) * float(size) - 0.5;
    float2 floored = floor(flat);
    float2 fraction = flat - floored;
    int2 base = int2(floored);
    float4 sum = float4(0.0);
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            int2 texel = WrapOctahedral(base + int2(x, y), int(size));
            float weight = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);
            sum += atlas.Load(origin + uint2(texel), 0u) * weight;
        }
    }

    return sum;
}

// What a world probe gives for a point near it: its weight after visibility and the window, and what it holds in a
// direction, from the radiance or the irradiance atlas.
struct ProbeRead
{
    float weight;
    float3 value;
}

ProbeRead ReadProbe(GiConstants gi, uint level, int3 probe, float3 point, float3 direction, bool wantsIrradiance)
{
    ProbeRead read;
    read.weight = 0.0;
    read.value = float3(0.0);
    uint cell = GetProbeCell(level, probe);
    ProbeState state = LoadProbeState(gi, cell);
    if ((state.flags & ProbeFlagValid) == 0u || any(state.world != probe))
        return read;

    // Chebyshev: how likely the probe sees past the point, from the distances it saw towards it.
    float3 probePosition = GetProbePosition(gi, level, probe);
    float3 toPoint = point - probePosition;
    float distanceToPoint = length(toPoint);
    float visibility = 1.0;
    if (distanceToPoint > Epsilon)
    {
        TextureRef distances = { gi.probeDistance };
        float2 moments = SampleOctahedral(distances, GetProbeAtlasOrigin(cell), ProbeTexels, toPoint / distanceToPoint).xy;
        float mean = moments.x;
        float variance = max(moments.y - mean * mean, 1e-4);
        if (distanceToPoint > mean)
        {
            float difference = distanceToPoint - mean;
            visibility = variance / (variance + difference * difference);
            visibility = max(visibility * visibility * visibility, 0.0);
        }
    }

    read.weight = max(visibility, 0.001);
    if (wantsIrradiance)
    {
        TextureRef irradiance = { gi.probeIrradiance };
        read.value = SampleOctahedral(irradiance, GetProbeIrradianceOrigin(cell), ProbeIrradianceTexels, direction).rgb;
    }
    else
    {
        TextureRef radiance = { gi.probeRadiance };
        read.value = SampleOctahedral(radiance, GetProbeAtlasOrigin(cell), ProbeTexels, direction).rgb;
    }

    return read;
}

// The world probes' value at a point in a direction, the eight probes around it in the finest level that holds
// them blended by distance and visibility; false when no probe has seen the point.
bool SampleWorldProbes(GiConstants gi, float3 point, float3 direction, bool wantsIrradiance, out float3 value)
{
    value = float3(0.0);
    for (uint level = 0u; level < ProbeLevels; level++)
    {
        float spacing = gi.probeSpacings[level];
        float3 scaled = point / spacing;
        int3 base = int3(floor(scaled));
        if (!IsInProbeWindow(gi, level, base, 0) || !IsInProbeWindow(gi, level, base + 1, 0))
            continue;

        float3 fraction = scaled - float3(base);
        float3 sum = float3(0.0);
        float weightSum = 0.0;
        for (uint i = 0u; i < 8u; i++)
        {
            int3 offset = int3(i & 1u, (i >> 1u) & 1u, (i >> 2u) & 1u);
            float3 trilinear = lerp(1.0 - fraction, fraction, float3(offset));
            float weight = trilinear.x * trilinear.y * trilinear.z;
            ProbeRead read = ReadProbe(gi, level, base + offset, point, direction, wantsIrradiance);
            sum += read.value * (weight * read.weight);
            weightSum += weight * read.weight;
        }

        if (weightSum > 1e-3)
        {
            value = sum / weightSum;
            return true;
        }
    }

    return false;
}

// The radiance the world probes hold at a point in a direction; false when none has seen it.
public bool SampleWorldProbeRadiance(GiConstants gi, float3 point, float3 direction, out float3 radiance)
{
    return SampleWorldProbes(gi, point, direction, false, radiance);
}

// The irradiance the world probes hold on a surface at a point facing a normal; false when none has seen it.
public bool SampleWorldProbeIrradiance(GiConstants gi, float3 point, float3 normal, out float3 irradiance)
{
    return SampleWorldProbes(gi, point, normal, true, irradiance);
}

// --- Screen probes ---

// How alike two surfaces are: one when on one plane with one normal, zero when apart, as a probe is weighed for a
// pixel or another probe.
public float GetProbePlaneWeight(float3 position, float3 normal, float viewDepth, float3 otherPosition, float3 otherNormal)
{
    float apart = abs(dot(normal, otherPosition - position));
    float tolerance = 0.05 + 0.02 * viewDepth;
    float planeWeight = saturate(1.0 - apart / tolerance);
    float normalWeight = saturate((dot(normal, otherNormal) - 0.8) / 0.2);
    return planeWeight * normalWeight;
}

public ScreenProbe LoadScreenProbe(uint table, uint index)
{
    ArrayRef<ScreenProbe> probes = { table };
    return probes[index];
}

public uint GetScreenProbeIndex(GiConstants gi, uint2 tile) { return (tile.y * gi.screenProbesX) + tile.x; }

// The texel at the top left of a screen probe's cell in the screen radiance atlases.
public uint2 GetScreenProbeOrigin(uint2 tile) { return tile * ScreenProbeTexels; }

// The direction a screen probe's texel keeps, in the frame of its normal: cosine-distributed over the hemisphere, so
// every texel stands for the same share of the irradiance.
public float3 GetScreenProbeDirection(float3x3 basis, uint2 texel, float2 jitter)
{
    float2 xi = (float2(texel) + jitter) / float(ScreenProbeTexels);
    float pdf;
    float3 local = SampleCosineHemisphere(xi, pdf);
    return ToWorld(basis, local);
}

// The disk sample that SampleConcentricDisk makes a point of the disk from.
float2 InverseConcentricDisk(float2 disk)
{
    float radius = length(disk);
    if (radius < Epsilon)
        return float2(0.5);

    float angle = atan2(disk.y, disk.x);
    if (angle < -Pi / 4.0)
        angle += TwoPi;

    float2 offset;
    if (angle < Pi / 4.0)
        offset = float2(radius, angle * radius / (Pi / 4.0));
    else if (angle < 3.0 * Pi / 4.0)
        offset = float2((HalfPi - angle) * radius / (Pi / 4.0), radius);
    else if (angle < 5.0 * Pi / 4.0)
        offset = float2(-radius, (angle - Pi) * -radius / (Pi / 4.0));
    else
        offset = float2((3.0 * HalfPi - angle) * -radius / (Pi / 4.0), -radius);

    return offset * 0.5 + 0.5;
}

// The texel of a screen probe that keeps a direction, as a position in its cell in texels, for the hemisphere of
// its normal; a direction below the surface gives the texel of the nearest one on it.
public float2 GetScreenProbeTexel(float3x3 basis, float3 direction)
{
    float3 local = ToLocal(basis, direction);
    float2 disk = local.xy;
    float diskLength = length(disk);
    if (local.z < 0.0 && diskLength > Epsilon)
        disk = disk / diskLength * 0.999;

    return InverseConcentricDisk(disk) * float(ScreenProbeTexels);
}

// A screen probe's cell at a position in texels, the four texels around it blended, clamped at the edges.
public float3 SampleScreenProbe(TextureRef atlas, uint2 origin, float2 texel)
{
    float2 shifted = clamp(texel - 0.5, float2(0.0), float2(ScreenProbeTexels - 1u));
    float2 floored = floor(shifted);
    float2 fraction = shifted - floored;
    uint2 base = uint2(floored);
    uint2 next = min(base + 1u, ScreenProbeTexels - 1u);
    float3 top = lerp(atlas.Load(origin + base, 0u).rgb, atlas.Load(origin + uint2(next.x, base.y), 0u).rgb, fraction.x);
    float3 bottom = lerp(atlas.Load(origin + uint2(base.x, next.y), 0u).rgb, atlas.Load(origin + next, 0u).rgb, fraction.x);
    return lerp(top, bottom, fraction.y);
}
