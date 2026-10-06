// The clipmap's layout, read from the GI state buffer the plan pass writes each frame. Included after the shader
// declared `StructuredBuffer<GpuGiState> GiState` (Include/Structs.hlsli) among its bindings, and after
// Gi/Common.hlsli. Every per-level texture stacks the levels along Z; the light volumes (GiShR, GiShG, GiShB) stack
// both light sets, each level's current light in the set its parity says (GiShUv, GiShTexel).

#ifndef MAGIC_GI_STATE_HLSLI
#define MAGIC_GI_STATE_HLSLI

uint GiLevels()
{
    return GiState[0].levels;
}

uint GiResolution()
{
    return GiState[0].resolution;
}

uint GiUpdateLevel()
{
    return GiState[0].updateLevel;
}

// Whether a level is rebuilt this frame: the plan rebuilds none on the frames between rebuilds, and every GI
// pass after it returns at once.
bool GiRebuilding()
{
    return GiUpdateLevel() < GiLevels();
}

float3 GiOrigin(uint level)
{
    return GiState[0].origins[level].xyz;
}

float GiVoxel(uint level)
{
    return GiState[0].origins[level].w;
}

float GiExtent(uint level)
{
    return GiVoxel(level) * (float)GiResolution();
}

bool GiLevelValid(uint level)
{
    return (GiState[0].valid & (1u << level)) != 0u;
}

bool GiLevelWasValid(uint level)
{
    return (GiState[0].extra.y & (1u << level)) != 0u;
}

// Which of the two light sets holds the level's current light.
uint GiParity(uint level)
{
    uint4 parity = GiState[0].parity;
    return level == 0u ? parity.x : level == 1u ? parity.y : level == 2u ? parity.z : parity.w;
}

// A world position in a level's voxel coordinates (0..resolution across the level).
float3 GiVoxelCoord(uint level, float3 world)
{
    return (world - GiOrigin(level)) / GiVoxel(level);
}

// The uv of a voxel coordinate in a stacked per-level texture, kept half a texel inside the level's slab so a
// trilinear sample never reads the neighbouring level.
float3 GiStackedUv(uint level, float3 voxel)
{
    float res = (float)GiResolution();
    float z = clamp(voxel.z, 0.5, res - 0.5) + (float)(level * GiResolution());
    return float3(clamp(voxel.xy, 0.5, res - 0.5) / res, z / (res * (float)GiLevels()));
}

int3 GiStackedTexel(uint level, uint3 voxel)
{
    return int3(voxel.x, voxel.y, voxel.z + level * GiResolution());
}

// The light volumes hold two sets of every level, slab parity * levels + level of 2 * levels along Z: the uv of a
// voxel coordinate of a level in one set, half a texel inside the slab as GiStackedUv keeps it.
float3 GiShUv(uint level, uint parity, float3 voxel)
{
    float res = (float)GiResolution();
    float slab = (float)(parity * GiLevels() + level);
    float z = clamp(voxel.z, 0.5, res - 0.5) + slab * res;
    return float3(clamp(voxel.xy, 0.5, res - 0.5) / res, z / (res * 2.0 * (float)GiLevels()));
}

int3 GiShTexel(uint level, uint parity, uint3 voxel)
{
    return int3(voxel.x, voxel.y, voxel.z + (parity * GiLevels() + level) * GiResolution());
}

uint GiVoxelIndex(uint3 voxel)
{
    return voxel.x + GiResolution() * (voxel.y + GiResolution() * voxel.z);
}

#endif
