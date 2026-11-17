// The probe grid of diffuse light (Majercik et al., "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance
// Fields", 2019): probes in a box over the world, each holding the light that reaches a surface facing each way and
// the distance to what it sees, as octahedral maps, the distances as means and squared means, and what a point's light
// from them is.
// The grid is IndirectLight's: cascades of the same grid centred on the camera at whole cells, each with cells twice as
// wide as the one inside it. A probe rests at the centre of its cell, so that surfaces at whole metres, which levels
// often have, hold none. A probe is kept in the slot of its world cell modulo the grid's size, so it keeps its slot
// and its light while the grid moves. The cascades' slots are stacked along z, so a probe's slot holds its cascade.
module "Light/Probes";

import Core.Frame;
import Core.Heap;
import Core.Math;
import Core.Pack;

// A probe's map of distances in the depth texture: a side of its directions, and of its tile, a texel of border
// around them.
public static const uint DepthTexels = 16u;
public static const uint DepthTile = DepthTexels + 2u;

public struct ProbeGridConstants
{
    // Probes along each axis of a cascade, and the cascades.
    public uint3 dimensions;
    public uint cascades;
    public uint probes;
    public uint depth;
    public uint depthSampler;
    // The metres between neighbouring probes of the finest cascade.
    public float3 cellSize;
}

// A side of a probe's octahedral map of light, C#'s Probes.IrradianceTexels.
public static const uint IrradianceTexels = 8u;

// A probe as the GPU keeps it, C#'s Probes.ProbeBytes long: its map of light, what a white diffuse surface facing each
// texel's way reflects, a texel a word packed as 11, 11 and 10-bit floats, row by row, and in xyz of offset how far it
// moved from where it rests, as a part of half a cell, in w 1 once it has blended rays.
public struct GpuProbe
{
    public uint4 irradiance[IrradianceTexels * IrradianceTexels / 4u];
    public float4 offset;
}

// Whether a probe has blended rays since the probes started afresh: one that hasn't holds no light yet and is read as
// none, and starts its light afresh when it first blends.
public bool HasBlended(GpuProbe probe) { return probe.offset.w != 0.0; }

float3 LoadProbeTexel(GpuProbe probe, uint2 texel)
{
    uint i = texel.x + texel.y * IrradianceTexels;
    return UnpackR11G11B10(probe.irradiance[i / 4u][i % 4u]);
}

// What a white diffuse surface facing a unit normal reflects of a probe's light: its map read between the four texels
// nearest the normal's way, held to the map's edge.
public float3 GetProbeTexelIrradiance(GpuProbe probe, float3 normal)
{
    float2 at = EncodeUnitVectorUnorm(normal) * float(IrradianceTexels) - 0.5;
    float2 f = saturate(at - floor(at));
    int2 corner = int2(floor(at));
    float3 light = float3(0.0);
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 offset = uint2(i & 1u, i >> 1u);
        uint2 texel = uint2(clamp(corner + int2(offset), int2(0), int2(IrradianceTexels - 1u)));
        float2 weight = lerp(1.0 - f, f, float2(offset));
        light += LoadProbeTexel(probe, texel) * weight.x * weight.y;
    }
    return light;
}

// The slot of a probe by its index.
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

// Cascade k of the grid: the same grid with cells 2^k times as wide.
public ProbeGridConstants GetCascade(ProbeGridConstants grid, uint k)
{
    grid.cellSize *= float(1u << k);
    return grid;
}

// The cascade of the probe in a slot.
public uint GetSlotCascade(ProbeGridConstants grid, uint3 slot) { return slot.z / grid.dimensions.z; }

// The world cell of the grid's first probe while the camera is at a point: the grid centred on the camera's cell.
public int3 GetFirstProbeCell(ProbeGridConstants grid, float3 camera)
{
    return int3(floor(camera / grid.cellSize)) - int3(grid.dimensions / 2u);
}

// The slot that keeps the probe of a world cell: the cell modulo the grid's size.
public uint3 GetProbeSlot(ProbeGridConstants grid, int3 cell)
{
    int3 size = int3(grid.dimensions);
    return uint3((cell % size + size) % size);
}

// The world cell of the probe in a slot, while the grid's first probe is at a cell. The slot's cascade drops out of the
// modulo.
public int3 GetProbeCell(ProbeGridConstants grid, uint3 slot, int3 first)
{
    return first + int3(GetProbeSlot(grid, int3(slot) - first));
}

// Where the probe of a world cell is: the cell's centre, moved by its offset.
public float3 GetProbePosition(ProbeGridConstants grid, int3 cell, GpuProbe probe)
{
    return (float3(cell) + 0.5 + probe.offset.xyz * 0.5) * grid.cellSize;
}

// Where a point is among the probes of cascade k, in cells from the grid's first probe.
float3 GetGridPosition(ProbeGridConstants grid, uint k, float3 position)
{
    ProbeGridConstants cascade = GetCascade(grid, k);
    return position / cascade.cellSize - 0.5 - float3(GetFirstProbeCell(cascade, GetFrame().cameraPosition.xyz));
}

// The texel of the depth texture a probe's map of distances starts at, inside its tile's border.
public uint2 GetProbeDepthTexel(ProbeGridConstants grid, uint3 coord)
{
    return coord.xz * DepthTile + uint2(coord.y * grid.dimensions.x * DepthTile, 0u) + 1u;
}

// Where the depth texture holds a probe's distances along a unit direction.
float2 GetProbeDepthUv(ProbeGridConstants grid, uint3 coord, float3 direction)
{
    uint3 size = grid.dimensions;
    float2 texels = float2(DepthTile * size.x * size.y, DepthTile * size.z * grid.cascades);
    float2 texel = float2(GetProbeDepthTexel(grid, coord)) + EncodeUnitVectorUnorm(direction) * DepthTexels;
    return texel / texels;
}

// What a white diffuse surface at a point facing a unit normal reflects of the light the eight probes of cascade k
// around it hold: its irradiance over pi. Each probe is weighted by how near it is, by whether it lies in front of the
// surface, and by whether its distances say it sees the point (Chebyshev's bound, as variance shadow maps test). The
// light fades to none as the probes in front of the surface stop seeing the point, so a closed room reads no light from
// outside.
// Past the cascade's edge the test fades out over a cell, as the edge probes' distances do not reach there.
float3 GetCascadeIrradiance(ProbeGridConstants grid, uint k, float3 position, float3 normal)
{
    ProbeGridConstants cascade = GetCascade(grid, k);
    int3 last = int3(grid.dimensions) - 1;
    int3 first = GetFirstProbeCell(cascade, GetFrame().cameraPosition.xyz);
    float3 cell = GetGridPosition(grid, k, position);
    float outside = saturate(Max3(abs(cell - clamp(cell, float3(0.0), float3(last)))));
    int3 base = clamp(int3(floor(cell)), int3(0), last);
    float3 alpha = saturate(cell - float3(base));
    TextureRef depthTexture = { grid.depth };
    SamplerRef depthSampler = { grid.depthSampler };
    float3 sum = float3(0.0);
    float sumWeight = 0.0;
    // Of the probes' weight by nearness and facing, the part that sees the point, and the whole.
    float seen = 0.0;
    float faced = 0.0;
    for (uint i = 0u; i < 8u; i++)
    {
        uint3 offset = uint3(i, i >> 1u, i >> 2u) & 1u;
        int3 probeCell = first + clamp(base + int3(offset), int3(0), last);
        uint3 coord = GetProbeSlot(grid, probeCell) + uint3(0u, 0u, k * grid.dimensions.z);
        GpuProbe probe = LoadProbe(grid, GetProbeIndex(grid, coord));
        if (!HasBlended(probe))
            continue;

        float3 probePosition = GetProbePosition(cascade, probeCell, probe);

        // A point a little off the surface, so the test is not made right where shadowed meets lit.
        float3 probeToPoint = position - probePosition + normal * 0.001;
        float3 direction = normalize(-probeToPoint);
        float3 trilinear = lerp(1.0 - alpha, alpha, float3(offset));

        // A soft back-face test, wrapped so small details whose normals face away from every probe still get some.
        float3 toProbe = NormalizeOrZero(probePosition - position);
        float wrap = Pow2(max(0.0001, (dot(toProbe, normal) + 1.0) * 0.5)) + 0.2;
        float facing = lerp(saturate(dot(direction, normal)), wrap, 0.01);

        float2 moments = depthTexture.SampleLevel(depthSampler, GetProbeDepthUv(grid, coord, -direction), 0.0).xy;
        float distance = length(probeToPoint);
        float variance = abs(Pow2(moments.x) - moments.y);
        float chebyshev = variance / (variance + Pow2(max(distance - moments.x, 0.0)));
        chebyshev = lerp(chebyshev, 1.0, outside);
        float visibility = distance <= moments.x ? 1.0 : max(Pow3(chebyshev), 0.0);
        float nearness = trilinear.x * trilinear.y * trilinear.z;
        seen += nearness * facing * visibility;
        faced += nearness * facing;
        float weight = facing * visibility;

        // Never quite nothing, and what is faint crushed further, as the eye sees a little light well.
        weight = max(0.01, weight);
        if (weight < 0.2)
            weight *= weight * weight / Pow2(0.2);

        weight *= nearness;
        sum += GetProbeTexelIrradiance(probe, normal) * weight;
        sumWeight += weight;
    }

    if (sumWeight <= 0.0)
        return float3(0.0);

    // Full light while a quarter of the facing weight sees the point, fading to none as none of it does.
    float coverage = saturate(4.0 * seen / max(faced, 1e-6)); // PROTO: the quarter is a guess, tune it by eye
    return sum / sumWeight * coverage;
}

// The cells between a point and the nearest face of the box of cascade k, less than 0 outside it.
float GetCellsInside(ProbeGridConstants grid, uint k, float3 position)
{
    float3 cell = GetGridPosition(grid, k, position);
    float3 inside = min(cell, float3(grid.dimensions) - 1.0 - cell);
    return min(inside.x, min(inside.y, inside.z));
}

// The cells at the edge of a cascade over which its light blends into the next coarser cascade's.
static const float CascadeBlendCells = 2.0;

// What a white diffuse surface at a point facing a unit normal reflects of the probes' light: its irradiance over pi,
// from the finest cascade whose box holds the point, blended into the next coarser one over its last cells. Past the
// coarsest box, the light of its edge probes.
public float3 GetProbeIrradiance(ProbeGridConstants grid, float3 position, float3 normal)
{
    uint k = 0u;
    float inside = GetCellsInside(grid, k, position);
    while (inside < 0.0 && k + 1u < grid.cascades)
    {
        k++;
        inside = GetCellsInside(grid, k, position);
    }

    float3 light = GetCascadeIrradiance(grid, k, position, normal);
    float finer = saturate(inside / CascadeBlendCells);
    if (k + 1u < grid.cascades && finer < 1.0)
        light = lerp(GetCascadeIrradiance(grid, k + 1u, position, normal), light, finer);

    return light;
}
