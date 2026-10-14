// Global illumination's scene and probes, as the renderer's GiVolume lays them out (GiLevels: a GpuGi, then a
// GpuGiLevel a level). The scene is a signed distance field in nested levels around the camera, each GiResolution
// voxels a side and twice the last one's voxel, stored wrapping around in one volume, the levels one after another
// along z; a voxel holds how far its middle is from the nearest surface, in metres, negative inside, and no more than
// GiReach voxels either way. Each level has a grid of GiProbes probes a side, one every GiStride voxels, which store
// the light arriving from every direction (DDGI: Majercik et al. 2019, "Dynamic Diffuse Global Illumination with
// Ray-Traced Irradiance Fields") on an octahedron: irradiance over pi in a tile of GiIrradianceSide texels, and the
// mean distance to what is in each direction and its square in one of GiDistanceSide, both with a one-texel border
// copied from inside so filtering never reads past a tile.
//
// What a function reads it declares by name, so a shader includes this and binds only what it uses: GiLevels always,
// GiField to sample the field, GiIrradiance and GiDistance to sample the probes.

#ifndef MAGIC_GI_HLSLI
#define MAGIC_GI_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Math.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Packing.hlsli"
#include "Include/Structs.hlsli"

static const uint GiResolution = 64u;
static const uint GiBrick = 8u;
static const uint GiProbes = 16u;
static const uint GiMaxLevels = 4u;
static const uint GiStride = GiResolution / GiProbes;
static const float GiReach = 4.0;
static const uint GiIrradianceSide = 8u;
static const uint GiDistanceSide = 16u;

// What a probe's irradiance is stored times, so a sunlit room's bounces, tens of thousands of nits, fit a half float.
static const float GiStoreScale = 1.0 / 32.0;

// What GiProbeState's w holds of a probe, besides the probe its slot holds in xyz: whether it lights anything this frame
// (Probes/GiClassify), and whether its tiles hold what it was traced with rather than nothing yet (Probes/GiBlend).
static const int GiProbeUsed = 2;
static const int GiProbeHeld = 1;

GpuGi GiHeader()
{
    return LOAD(GiLevels, GpuGi, 0u);
}

GpuGiLevel GiLevel(uint level)
{
    return LOAD(GiLevels, GpuGiLevel, 1u + level);
}

int3 GiWrap(int3 value, int size)
{
    return ((value % size) + size) % size;
}

// The voxel of a level a place in metres is in, from the world's origin.
int3 GiVoxelOf(GpuGiLevel level, float3 position)
{
    return (int3)floor(position / level.voxel);
}

// Whether a level holds a place, at least margin voxels in from its faces.
bool GiContains(GpuGiLevel level, float3 position, float margin)
{
    float3 voxels = position / level.voxel - float3(level.origin);
    return level.voxel > 0.0 && all(voxels >= margin) && all(voxels < (float)GiResolution - margin);
}

// Where a voxel of a level is stored, and its place in the buffers that hold one value a voxel.
uint3 GiStored(int3 voxel, uint level)
{
    uint3 wrapped = (uint3)GiWrap(voxel, (int)GiResolution);
    return uint3(wrapped.xy, wrapped.z + level * GiResolution);
}

uint GiIndex(uint3 stored)
{
    return stored.x + GiResolution * (stored.y + GiResolution * stored.z);
}

// The voxel stored at a place of a level's volume, from the world's origin.
int3 GiVoxelAt(GpuGiLevel level, uint3 stored)
{
    int3 local = int3(stored.xy, stored.z % GiResolution);
    return level.origin + GiWrap(local - level.origin, (int)GiResolution);
}

// Whether a voxel's brick is made again this frame.
bool GiIsDirty(GpuGiLevel level, int3 voxel)
{
    uint3 brick = (uint3)GiWrap(voxel, (int)GiResolution) / GiBrick;
    uint bit = brick.x + 8u * (brick.y + 8u * brick.z);
    uint word = bit / 32u;
    return ((level.dirty[word / 4u][word % 4u] >> (bit % 32u)) & 1u) != 0u;
}

// A distance as a uint that sorts as the distance does, so the nearest of several is an atomic minimum.
uint GiEncode(float distance)
{
    uint bits = asuint(distance);
    return (bits & 0x80000000u) != 0u ? ~bits : bits | 0x80000000u;
}

float GiDecode(uint code)
{
    return asfloat((code & 0x80000000u) != 0u ? code & 0x7FFFFFFFu : ~code);
}

// A probe's place in metres, from its place in its level's grid from the world's origin; and the level's first probe.
float3 GiProbePosition(GpuGiLevel level, int3 probe)
{
    return (float3(probe) + 0.5) * (float)GiStride * level.voxel;
}

int3 GiProbeOrigin(GpuGiLevel level)
{
    return level.origin / (int)GiStride;
}

// A probe's index, by where it is stored, and its tile in the atlases.
uint GiProbeIndex(int3 probe, uint level)
{
    uint3 stored = (uint3)GiWrap(probe, (int)GiProbes);
    return stored.x + GiProbes * (stored.y + GiProbes * (stored.z + GiProbes * level));
}

uint2 GiTile(int3 probe, uint level)
{
    uint3 stored = (uint3)GiWrap(probe, (int)GiProbes);
    return uint2(stored.x + GiProbes * stored.y, stored.z + GiProbes * level);
}

// The probe an index stands for this frame, and its level.
int3 GiProbeOf(uint index, out uint level, out GpuGiLevel info)
{
    level = index / (GiProbes * GiProbes * GiProbes);
    info = GiLevel(level);
    int3 stored = int3(index % GiProbes, (index / GiProbes) % GiProbes, (index / (GiProbes * GiProbes)) % GiProbes);
    int3 origin = GiProbeOrigin(info);
    return origin + GiWrap(stored - origin, (int)GiProbes);
}

// The probe the slot-th of this frame's traced probes is, false past the last: a level's probes are traced every
// period frames (GpuGiLevel.period), a coarser level's more seldom, as its probes light what is further and larger,
// and which share of them this frame from the frame's number.
bool GiDueProbe(GpuGi header, uint slot, out uint probeIndex)
{
    uint perLevel = GiProbes * GiProbes * GiProbes;
    probeIndex = 0u;
    [loop] for (uint index = 0u; index < header.levels; index++)
    {
        uint period = max(GiLevel(index).period, 1u);
        uint count = (perLevel + period - 1u) / period;
        if (slot < count)
        {
            uint inLevel = slot * period + header.seed % period;
            probeIndex = index * perLevel + inLevel;
            return inLevel < perLevel;
        }

        slot -= count;
    }

    return false;
}

// Where in a probe's tile a direction is, in uv of an atlas size texels.
float2 GiOctUv(uint2 tile, float3 direction, uint side, float2 size)
{
    float2 octahedron = OctEncode(direction) * 0.5 + 0.5;
    return (float2(tile * side) + 1.0 + octahedron * (float)(side - 2u)) / size;
}

// The direction a ray of a probe goes: rays spread evenly over the sphere (a spherical Fibonacci set), all turned by
// one rotation a frame (Shoemake 1992, a uniformly random one from seed), so each time a probe is traced its rays
// cover the gaps of the time before.
float3 GiRayDirection(uint ray, uint rays, uint seed)
{
    float z = 1.0 - (2.0 * (float)ray + 1.0) / (float)rays;
    float radius = sqrt(saturate(1.0 - z * z));
    float phi = (float)ray * 2.39996323;
    float3 direction = float3(radius * cos(phi), z, radius * sin(phi));
    float u1 = HashToUnit(PcgHash(seed * 3u));
    float u2 = HashToUnit(PcgHash(seed * 3u + 1u));
    float u3 = HashToUnit(PcgHash(seed * 3u + 2u));
    float4 q = float4(sqrt(1.0 - u1) * sin(TwoPi * u2), sqrt(1.0 - u1) * cos(TwoPi * u2), sqrt(u1) * sin(TwoPi * u3), sqrt(u1) * cos(TwoPi * u3));
    return direction + 2.0 * cross(q.xyz, cross(q.xyz, direction) + q.w * direction);
}

// Which texel inside a probe's tile one on its border copies, the tile side texels with its inside from 1 to side - 2:
// the octahedron folds over its edges, so a border texel is the inside one across the fold.
uint2 GiBorderSource(uint2 at, uint side)
{
    uint last = side - 1u;
    bool isLeft = at.x == 0u;
    bool isRight = at.x == last;
    bool isTop = at.y == 0u;
    bool isBottom = at.y == last;
    if ((isLeft || isRight) && (isTop || isBottom))
    {
        return uint2(isLeft ? last - 1u : 1u, isTop ? last - 1u : 1u);
    }

    if (isTop || isBottom)
    {
        return uint2(last - at.x, isTop ? 1u : last - 1u);
    }

    if (isLeft || isRight)
    {
        return uint2(isLeft ? 1u : last - 1u, last - at.y);
    }

    return at;
}

// The direction an inside texel of a probe's tile stands for.
float3 GiTexelDirection(uint2 source, uint side)
{
    return OctDecode(((float2(source) - 0.5) / (float)(side - 2u)) * 2.0 - 1.0);
}

#if defined(HAS_GiField)
// The distance at a place from a level's voxels, trilinear between their middles; at the seam where the level wraps
// around, the nearest voxel's.
float GiFieldAt(GpuGiLevel level, uint index, float3 position)
{
    float3 voxels = position / level.voxel - 0.5;
    float3 wrapped = clamp(voxels - floor(voxels / (float)GiResolution) * (float)GiResolution, 0.0, (float)GiResolution - 1.0);
    float3 uvw = float3((wrapped.xy + 0.5) / (float)GiResolution, (wrapped.z + 0.5 + (float)(index * GiResolution)) / (float)(GiResolution * GiMaxLevels));
    return GiField.SampleLevel(GiFieldSampler, uvw, 0.0);
}

// Which way the field grows at a place: the surface's normal there.
float3 GiNormalAt(GpuGiLevel level, uint index, float3 position)
{
    float step = level.voxel;
    float3 gradient = float3(
        GiFieldAt(level, index, position + float3(step, 0.0, 0.0)) - GiFieldAt(level, index, position - float3(step, 0.0, 0.0)),
        GiFieldAt(level, index, position + float3(0.0, step, 0.0)) - GiFieldAt(level, index, position - float3(0.0, step, 0.0)),
        GiFieldAt(level, index, position + float3(0.0, 0.0, step)) - GiFieldAt(level, index, position - float3(0.0, 0.0, step)));
    return dot(gradient, gradient) > 1e-12 ? normalize(gradient) : float3(0.0, 1.0, 0.0);
}

// Whether a level's probe lights anything this frame: not inside something, or so near it that its rays start at its
// surface, and with something the field holds near enough to be lit by it. A place lit by probes reads the eight around
// where it is pushed off its surface, by at most 0.3 of their spacing (GiSampleLevel), so a probe lights only what is
// within 1.3 spacings of it along each axis: a surface the view shows, a little inside the field's (Probes/GiJobs.hlsli),
// or where another probe's ray stops, on it. The field is read on a grid of 4 x 4 x 4 over that box, each point vouching
// for its share of the box when it says nothing is nearer than that share's corner and a voxel more, within the
// GiReach voxels the field says anything for; a point past the level is read at its edge.
bool GiIsUsed(GpuGiLevel level, uint index, int3 probe)
{
    float3 centre = GiProbePosition(level, probe);
    if (level.voxel <= 0.0 || GiFieldAt(level, index, centre) <= 0.5 * level.voxel)
    {
        return false;
    }

    float share = 2.6 * (float)GiStride * level.voxel / 4.0;
    float clear = 0.5 * sqrt(3.0) * share + level.voxel;
    float3 low = (float3(level.origin) + 0.5) * level.voxel;
    float3 high = (float3(level.origin) + (float)GiResolution - 0.5) * level.voxel;
    [loop] for (uint place = 0u; place < 64u; place++)
    {
        float3 offset = (float3(uint3(place & 3u, (place >> 2u) & 3u, place >> 4u)) - 1.5) * share;
        if (GiFieldAt(level, index, clamp(centre + offset, low, high)) <= clear)
        {
            return true;
        }
    }

    return false;
}

struct GiHit
{
    bool isHit;
    bool isInside;
    float distance;
    uint level;
    float3 position;
};

// A ray through the field, stepping as far as the nearest surface each time (sphere tracing, Hart 1996), in the finest
// level from first on that holds where it is; no hit once it leaves the coarsest or goes maxDistance.
GiHit GiTrace(float3 origin, float3 direction, uint first, uint levels, float maxDistance)
{
    GiHit hit;
    hit.isHit = false;
    hit.isInside = false;
    hit.distance = maxDistance;
    hit.level = first;
    hit.position = origin;
    uint index = first;
    GpuGiLevel level = GiLevel(index);
    float t = 0.0;
    [loop] for (uint step = 0u; step < 128u && t < maxDistance; step++)
    {
        float3 position = origin + direction * t;
        [loop] while (index < levels && !GiContains(level, position, 1.0))
        {
            index++;
            level = GiLevel(min(index, levels - 1u));
        }

        if (index >= levels)
        {
            break;
        }

        float distance = GiFieldAt(level, index, position);
        if (distance < 0.2 * level.voxel)
        {
            hit.isHit = true;
            hit.isInside = step == 0u && distance < 0.0;
            hit.distance = t;
            hit.level = index;
            hit.position = position;
            return hit;
        }

        t += max(distance, 0.2 * level.voxel);
    }

    return hit;
}

// How much of the sun reaches a place through the field, softened by how near the ray passes what it misses
// (Quilez 2010's soft shadows from a distance field).
float GiSunVisibility(float3 origin, float3 toSun, uint first, uint levels)
{
    uint index = first;
    GpuGiLevel level = GiLevel(index);
    float maxDistance = GiLevel(levels - 1u).voxel * (float)GiResolution * 0.5;
    float visible = 1.0;
    float t = level.voxel;
    [loop] for (uint step = 0u; step < 64u && t < maxDistance; step++)
    {
        float3 position = origin + toSun * t;
        [loop] while (index < levels && !GiContains(level, position, 1.0))
        {
            index++;
            level = GiLevel(min(index, levels - 1u));
        }

        if (index >= levels)
        {
            break;
        }

        float distance = GiFieldAt(level, index, position);
        if (distance < 0.05 * level.voxel)
        {
            return 0.0;
        }

        visible = min(visible, 8.0 * distance / t);
        t += max(distance, 0.25 * level.voxel);
    }

    return saturate(visible);
}
#endif

#if defined(HAS_GiIrradiance) && defined(HAS_GiDistance)
// The irradiance over pi a level's probes give a surface at a place, facing normal, seen from view (toward the eye; the
// normal again where there is no eye), and how much of it they vouch for: the eight probes around it, each counted by
// how near it is, how much it faces the surface, and whether the surface is in sight of it by what it saw that way
// (Chebyshev's bound on its distances), so light does not come through walls; a probe inside something counts for
// nothing. Where none of them has the surface in sight, the near and facing ones count as they are rather than none:
// light from everywhere never falls back to the open sky inside a level.
float GiSampleLevel(GpuGiLevel level, uint index, float3 position, float3 normal, float3 view, out float3 irradiance)
{
    float width;
    float height;
    GiIrradiance.GetDimensions(width, height);
    float distanceWidth;
    float distanceHeight;
    GiDistance.GetDimensions(distanceWidth, distanceHeight);
    float spacing = (float)GiStride * level.voxel;
    float3 biased = position + (normal * 0.2 + view * 0.8) * (0.3 * spacing);
    float3 grid = biased / spacing - 0.5;
    int3 base = (int3)floor(grid);
    float3 alpha = grid - float3(base);
    float3 sum = float3(0.0, 0.0, 0.0);
    float total = 0.0;
    float3 nearSum = float3(0.0, 0.0, 0.0);
    float nearTotal = 0.0;
    [unroll] for (uint corner = 0u; corner < 8u; corner++)
    {
        int3 offset = int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
        int3 probe = base + offset;
        float3 probePosition = GiProbePosition(level, probe);
        float3 toProbe = normalize(probePosition - position);
        float3 trilinear = select(offset != 0, alpha, 1.0 - alpha);
        float facing = (dot(toProbe, normal) + 1.0) * 0.5;
        float weight = trilinear.x * trilinear.y * trilinear.z * (facing * facing + 0.2);
        float near = weight;
        uint2 tile = GiTile(probe, index);
        float3 fromProbe = biased - probePosition;
        float distance = length(fromProbe);
        float2 moments = GiDistance.SampleLevel(GiDistanceSampler, GiOctUv(tile, fromProbe / max(distance, 1e-4), GiDistanceSide, float2(distanceWidth, distanceHeight)), 0.0).xy;
        if (distance > moments.x)
        {
            float variance = abs(moments.x * moments.x - moments.y);
            float beyond = distance - moments.x;
            float chebyshev = variance / (variance + beyond * beyond);
            weight *= max(chebyshev * chebyshev * chebyshev, 0.0);
        }

        // Crush the weights that are left small, so a probe that barely sees the surface does not show.
        weight = max(weight, 1e-6);
        weight = weight < 0.2 ? weight * weight * weight / 0.04 : weight;
        float4 stored = GiIrradiance.SampleLevel(GiIrradianceSampler, GiOctUv(tile, normal, GiIrradianceSide, float2(width, height)), 0.0);
        weight *= stored.a;
        near *= stored.a;
        sum += stored.rgb * (weight / GiStoreScale);
        total += weight;
        nearSum += stored.rgb * (near / GiStoreScale);
        nearTotal += near;
    }

    irradiance = total > 1e-4 ? sum / total : nearTotal > 0.0 ? nearSum / nearTotal : float3(0.0, 0.0, 0.0);
    return nearTotal;
}

// The irradiance over pi the probes give a surface, from the finest level that holds the place, blended into the next
// over its outermost probes, and how much of it the levels hold: 1 well inside them, fading to 0 past the coarsest.
float3 GiIrradianceAt(float3 position, float3 normal, float3 view, out float held)
{
    GpuGi header = GiHeader();
    float3 result = float3(0.0, 0.0, 0.0);
    float remaining = 1.0;
    [loop] for (uint index = 0u; index < header.levels && remaining > 0.0; index++)
    {
        GpuGiLevel level = GiLevel(index);
        if (!GiContains(level, position, (float)GiStride))
        {
            continue;
        }

        float3 voxels = position / level.voxel - float3(level.origin);
        float edge = min(min(voxels.x, min(voxels.y, voxels.z)), min((float)GiResolution - voxels.x, min((float)GiResolution - voxels.y, (float)GiResolution - voxels.z)));
        float3 irradiance;
        float weight = GiSampleLevel(level, index, position, normal, view, irradiance);
        float share = remaining * saturate(edge / (float)GiStride - 1.0) * (weight > 1e-5 ? 1.0 : 0.0);
        result += irradiance * share;
        remaining -= share;
    }

    held = 1.0 - remaining;
    return held > 0.0 ? result / held : result;
}
#endif

#endif
