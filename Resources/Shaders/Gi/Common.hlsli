// What the GI passes share: the clipmap's layout as the lighting constants carry it (Include/Shade.hlsli), the
// brick atlas, the accumulation buffer one level is stamped through, the light grid, and the spherical
// harmonics the bounced light is kept as. Needs Include/Shade.hlsli included first.
//
// A level is GiResolution voxels a side, its min corner at ClipmapOrigin[level].xyz (absolute world) and its
// voxel ClipmapOrigin[level].w metres. Every per-level texture holds the levels stacked along Z, level l's
// slab from l * GiResolution to (l + 1) * GiResolution, so one binding serves whatever the level count is.

#ifndef MAGIC_GI_COMMON_HLSLI
#define MAGIC_GI_COMMON_HLSLI

#include "Include/Structs.hlsli"

// Twins: GiVolumes.BrickSize, BricksAcross, BricksDeep, AccumWords, LightCells, LightsPerCell, TaskChunk.
#define GI_BRICK 16u
#define GI_BRICKS_ACROSS 16u
#define GI_BRICKS_DEEP 8u
#define GI_ACCUM_WORDS 8u
#define GI_LIGHT_CELLS 16u
#define GI_LIGHTS_PER_CELL 32u
#define GI_TASK_CHUNK 4u
#define GI_TASK_CAPACITY 65536u

// Distances in the field are stored as a share of this many voxels.
static const float GiSdfMaxVoxels = 16.0;

// Below this much of a voxel filled, it is air.
static const float GiOccupied = 0.5;

// 48 B: a draw group's mesh box in the mesh's own space, and the mesh's occupancy brick (0xFFFFFFFF for none).
struct GpuGiGroup
{
    float4 boxMin;
    float4 boxMax;
    uint brick;
    uint meshSlot;
    uint pad0;
    uint pad1;
};

// 32 B: what a material gives the GI: its albedo and what it emits, linear.
struct GpuGiMaterial
{
    float4 albedo;
    float4 emissive;
};

// 48 B: a vertex of the mega vertex buffer read as three float4 (Vertex: position, normal, tangent, uv).
struct GpuVertexRaw
{
    float4 a;
    float4 b;
    float4 c;
};

static const uint GiNoBrick = 0xFFFFFFFFu;

// The clipmap's layout, from the lighting constants: only where Include/Shade.hlsli came first (the brick passes
// carry their own block and need none of it).
#ifdef MAGIC_SHADE_HLSLI
float3 GiOrigin(uint level)
{
    return ClipmapOrigin[level].xyz;
}

float GiVoxel(uint level)
{
    return ClipmapOrigin[level].w;
}

float GiExtent(uint level)
{
    return GiVoxel(level) * (float)GiResolution;
}

// A world position in a level's voxel coordinates (0..GiResolution across the level).
float3 GiVoxelCoord(uint level, float3 world)
{
    return (world - GiOrigin(level)) / GiVoxel(level);
}

// The uv of a voxel coordinate in a stacked per-level texture, kept half a texel inside the level's slab so a
// trilinear sample never reads the neighbouring level.
float3 GiStackedUv(uint level, float3 voxel)
{
    float res = (float)GiResolution;
    float z = clamp(voxel.z, 0.5, res - 0.5) + (float)(level * GiResolution);
    return float3(clamp(voxel.xy, 0.5, res - 0.5) / res, z / (res * (float)GiLevels));
}

int3 GiStackedTexel(uint level, uint3 voxel)
{
    return int3(voxel.x, voxel.y, voxel.z + level * GiResolution);
}

uint GiVoxelIndex(uint3 voxel)
{
    return voxel.x + GiResolution * (voxel.y + GiResolution * voxel.z);
}

bool GiLevelValid(uint level)
{
    return (GiFlags & (256u << level)) != 0u;
}
#endif

uint3 GiBrickOffset(uint brick)
{
    return uint3(brick % GI_BRICKS_ACROSS, (brick / GI_BRICKS_ACROSS) % GI_BRICKS_ACROSS, brick / (GI_BRICKS_ACROSS * GI_BRICKS_ACROSS)) * GI_BRICK;
}

// The brick atlas uv of a point in a brick, given in 0..1 across the brick.
float3 GiBrickUv(uint brick, float3 inBrick)
{
    float3 texel = float3(GiBrickOffset(brick)) + clamp(inBrick, 0.0, 1.0) * (float)GI_BRICK;
    return texel / float3(GI_BRICKS_ACROSS * GI_BRICK, GI_BRICKS_ACROSS * GI_BRICK, GI_BRICKS_DEEP * GI_BRICK);
}

// The light grid: the level cut into GI_LIGHT_CELLS cells a side, a row of a count then GI_LIGHTS_PER_CELL light
// indices per cell.
uint GiLightCellIndex(uint3 cell)
{
    return cell.x + GI_LIGHT_CELLS * (cell.y + GI_LIGHT_CELLS * cell.z);
}

uint GiLightRow(uint3 cell)
{
    return GiLightCellIndex(cell) * (GI_LIGHTS_PER_CELL + 1u);
}

// The light as spherical harmonics, first two bands, one float4 per colour channel: x the constant term, yzw the
// linear ones. A lobe of unit light arriving from direction, and the light a surface facing normal gathers from it
// (scaled so one unit arriving straight on gives about three quarters, the rest to the sides).
float4 ShLobe(float3 direction)
{
    return float4(0.28209, 0.48860 * direction);
}

float ShIrradiance(float4 sh, float3 normal)
{
    return max(0.0, 0.88623 * sh.x + 1.02333 * dot(sh.yzw, normal));
}

// What a voxel's three accumulated colours and coverage become.
void GiUnpackAccum(uint coverage, uint r, uint g, uint b, out float cov, out float3 albedo)
{
    cov = saturate((float)coverage / 255.0);
    albedo = coverage > 0u ? float3(r, g, b) / (float)coverage : float3(0.0, 0.0, 0.0);
}

#endif
