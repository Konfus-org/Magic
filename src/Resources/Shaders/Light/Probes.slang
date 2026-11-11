// The probe grid of diffuse light (Majercik et al., "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance
// Fields", 2019): probes in a box over the world, each holding the light that reaches it as L1 spherical harmonics and
// the distance to what it sees as an octahedral map of means and squared means, and what a point's light from them is.
// The grid is IndirectLight's, placed each frame over the traced world's box by what traces it.
module "Light/Probes";

import Core.Heap;
import Core.Math;
import Core.Pack;

// The most rays a probe traces in a frame, and the step its count of rays moves in, also the fewest it asks for.
public static const uint MaxProbeRays = 256u;
public static const uint ProbeRayStep = 4u;

// The directions a probe blends its light in, a side of its octahedral map.
public static const uint IrradianceTexels = 8u;

// A probe's map of distances in the depth texture: a side of its directions, and of its tile, a texel of border
// around them.
public static const uint DepthTexels = 16u;
public static const uint DepthTile = DepthTexels + 2u;

// How far a probe moves from a surface it is near, as a part of its depth's reach.
public static const float KeepDistance = 0.1;

public struct ProbeGridConstants
{
    // Probes along each axis.
    public uint3 dimensions;
    public uint probes;
    public uint depth;
    public uint depthSampler;
    public uint rays;
    public uint rayList;
    public uint rayArguments;
    public uint rayCounts;
    public uint variance;
    // 0 while the probes hold nothing: this update starts them afresh.
    public uint hasHistory;
    // Where the first probe rests in the world, how far a probe's depth reaches, one and a half cells, and the
    // metres between neighbouring probes.
    public float3 origin;
    public float maxDistance;
    public float3 cellSize;
}

// The ray counts buffer's words: the rays the probes were given this frame in all at 0, then each probe's count, none
// while it waits its turn, and where its rays start among the frame's.
public uint GetProbeRayCountOffset(uint probe) { return 4u + probe * 8u; }
public uint GetProbeRayStartOffset(uint probe) { return 8u + probe * 8u; }

// A probe as the GPU keeps it, C#'s Probes.ProbeBytes long: its light's spherical harmonics in xyz of each, and in xyz of offset
// how far it moved from where it rests, as a part of half a cell, in w 1 once it has blended rays.
public struct GpuProbe
{
    public float4 radiance[4];
    public float4 offset;
}

// Whether a probe has blended rays since the probes started afresh: one that hasn't holds no light yet and is read as
// none, and starts its light afresh when it first blends.
public bool HasBlended(GpuProbe probe) { return probe.offset.w != 0.0; }

// L1 spherical harmonics of colour: the constant, then the y, z and x bands.
public struct ProbeSh
{
    public float3 c[4];
}

static const float ShBasis0 = 0.282094792;
static const float ShBasis1 = 0.488602512;

public ProbeSh CreateProbeSh(GpuProbe probe)
{
    ProbeSh sh;
    for (uint i = 0u; i < 4u; i++)
    {
        sh.c[i] = probe.radiance[i].xyz;
    }
    return sh;
}

// The harmonics of a value seen along a unit direction.
public ProbeSh ProjectOntoSh(float3 direction, float3 value)
{
    ProbeSh sh;
    sh.c[0] = ShBasis0 * value;
    sh.c[1] = ShBasis1 * direction.y * value;
    sh.c[2] = ShBasis1 * direction.z * value;
    sh.c[3] = ShBasis1 * direction.x * value;
    return sh;
}

// The radiance the harmonics hold along a unit direction.
public float3 EvaluateSh(ProbeSh sh, float3 direction)
{
    return ShBasis0 * sh.c[0] + ShBasis1 * (direction.y * sh.c[1] + direction.z * sh.c[2] + direction.x * sh.c[3]);
}

public uint3 GetProbeCoord(ProbeGridConstants grid, uint index)
{
    uint3 size = grid.dimensions;
    return uint3(index % size.x, (index / size.x) % size.y, index / (size.x * size.y));
}

public uint GetProbeIndex(ProbeGridConstants grid, uint3 coord)
{
    uint3 size = grid.dimensions;
    return coord.x + coord.y * size.x + coord.z * size.x * size.y;
}

public GpuProbe LoadProbe(ProbeGridConstants grid, uint index)
{
    ArrayRef<GpuProbe> probes = { grid.probes };
    return probes[index];
}

// Where a probe is in the world: where it rests, moved by its offset.
public float3 GetProbePosition(ProbeGridConstants grid, uint3 coord, GpuProbe probe)
{
    return grid.origin + float3(coord) * grid.cellSize + probe.offset.xyz * grid.cellSize * 0.5;
}

// The texel of the depth texture a probe's map of distances starts at, inside its tile's border.
public uint2 GetProbeDepthTexel(ProbeGridConstants grid, uint3 coord)
{
    return coord.xz * DepthTile + uint2(coord.y * grid.dimensions.x * DepthTile, 0u) + 1u;
}

// Where the depth texture holds a probe's distances along a unit direction.
float2 GetProbeDepthUv(ProbeGridConstants grid, uint3 coord, float3 direction)
{
    float2 size = float2(DepthTile * grid.dimensions.x * grid.dimensions.y, DepthTile * grid.dimensions.z);
    float2 texel = float2(GetProbeDepthTexel(grid, coord)) + (EncodeOctahedral(direction) * 0.5 + 0.5) * DepthTexels;
    return texel / size;
}

// What a white diffuse surface at a point facing a unit normal reflects of the light the eight probes around it hold:
// its irradiance over pi. Each probe is weighted by how near it is, by whether it lies in front of the surface, and by
// whether its distances say it sees the point (Chebyshev's bound, as variance shadow maps test).
public float3 GetProbeIrradiance(ProbeGridConstants grid, float3 position, float3 normal)
{
    int3 last = int3(grid.dimensions) - 1;
    float3 cell = (position - grid.origin) / grid.cellSize;
    int3 base = clamp(int3(floor(cell)), int3(0), last);
    float3 alpha = saturate(cell - float3(base));
    TextureRef depthTexture = { grid.depth };
    SamplerRef depthSampler = { grid.depthSampler };
    ProbeSh sum = {};
    float sumWeight = 0.0;
    for (uint i = 0u; i < 8u; i++)
    {
        uint3 offset = uint3(i, i >> 1u, i >> 2u) & 1u;
        uint3 coord = uint3(clamp(base + int3(offset), int3(0), last));
        GpuProbe probe = LoadProbe(grid, GetProbeIndex(grid, coord));
        if (!HasBlended(probe))
            continue;

        float3 probePosition = GetProbePosition(grid, coord, probe);

        // A point a little off the surface, so the test is not made right where shadowed meets lit.
        float3 probeToPoint = position - probePosition + normal * 0.001;
        float3 direction = normalize(-probeToPoint);
        float3 trilinear = lerp(1.0 - alpha, alpha, float3(offset));

        // A soft back-face test, wrapped so small details whose normals face away from every probe still get some.
        float3 toProbe = NormalizeOrZero(probePosition - position);
        float wrap = Pow2(max(0.0001, (dot(toProbe, normal) + 1.0) * 0.5)) + 0.2;
        float weight = lerp(saturate(dot(direction, normal)), wrap, 0.01);

        float2 moments = depthTexture.SampleLevel(depthSampler, GetProbeDepthUv(grid, coord, -direction), 0.0).xy;
        float distance = length(probeToPoint);
        float variance = abs(Pow2(moments.x) - moments.y);
        float chebyshev = variance / (variance + Pow2(max(distance - moments.x, 0.0)));
        weight *= distance <= moments.x ? 1.0 : max(Pow3(chebyshev), 0.0);

        // Never quite nothing, and what is faint crushed further, as the eye sees a little light well.
        weight = max(0.01, weight);
        if (weight < 0.2)
            weight *= weight * weight / Pow2(0.2);

        weight *= trilinear.x * trilinear.y * trilinear.z;
        ProbeSh sh = CreateProbeSh(probe);
        for (uint band = 0u; band < 4u; band++)
        {
            sum.c[band] += sh.c[band] * weight;
        }
        sumWeight += weight;
    }

    if (sumWeight <= 0.0)
        return float3(0.0);

    // The radiance convolved with the cosine lobe and read along the normal: the irradiance (Ramamoorthi and
    // Hanrahan, "An Efficient Representation for Irradiance Environment Maps", 2001).
    float3 irradiance = ShBasis0 * Pi * sum.c[0]
        + ShBasis1 * (TwoPi / 3.0) * (normal.y * sum.c[1] + normal.z * sum.c[2] + normal.x * sum.c[3]);
    return max(irradiance / sumWeight, 0.0) * InvPi;
}
