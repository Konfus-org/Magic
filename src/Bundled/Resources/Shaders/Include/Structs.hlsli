// The rows of the renderer's GPU tables, as its GpuStructs.cs (and Materials.cs's GpuMaterial) lays them out member for
// member: change both together. Every member group is 16 bytes, which packs the same in every API's storage buffers.
// A transform is three float4 rows of transpose(world), applied with dot(): storage buffers carry no matrix types.

#ifndef MAGIC_STRUCTS_HLSLI
#define MAGIC_STRUCTS_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Math.hlsli"

struct GpuMeshField
{
    float3 min;
    float padding0;
    float3 size;
    float padding1;
    float3 corner;
    float padding2;
};

// A point or spot light a view holds, nearest first, which only Shaders/Lights/LightPlace.comp.hlsl writes, so it has
// no C# twin: where it is relative to the camera, one over its reach squared, its intensity, bulb radius, axis and
// reach, a spot's cone's falloff, its depth in the view, whether it is a spot, and its slot in the world's table, which
// its shadow's pages are kept by, NoShadowFace for none.
struct GpuLight
{
    float3 position;
    float invRangeSquared;
    float3 intensity;
    float radius;
    float3 forward;
    float range;
    float spotScale;
    float spotOffset;
    float viewDepth;
    uint isSpot;
    uint shadow;
    uint3 padding;
};

static const uint NoShadowFace = 0xFFFFFFFFu;

struct GpuMaterial
{
    uint4 rows[8];
};

struct GpuMaterialGi
{
    float4 color;
    float4 emission;
};

// The first row of GiLevels; a GpuGiLevel for each level of the scene's field after it.
struct GpuGi
{
    uint levels;
    uint rays;
    uint seed;
    uint padding0;
    float3 camera;
    uint padding1;
    uint4 padding[5];
};

struct GpuGiLevel
{
    int3 origin;
    float voxel;
    int3 dirtyMin;
    float hysteresis;
    int3 dirtyMax;
    uint period;
    uint4 dirty[4];
};

// A scene instance's flags.
static const uint SceneMirrored = 1u;
static const uint SceneFadeIn = 2u;

// A scene instance, by its entity's slot: the rows of transpose(world) (xyz its turn and scale, w its position's high
// float, which low adds to, so a far position is still exact relative to the camera), and the last frame's the same way;
// its largest scale, its model's row, when its fade started (in Time) and its eight material slots' rows, two to a word,
// 0xFFFF for one not in yet.
struct GpuSceneInstance
{
    float4 row0;
    float4 row1;
    float4 row2;
    float3 low;
    float maxScale;
    float4 prevRow0;
    float4 prevRow1;
    float4 prevRow2;
    float3 prevLow;
    uint flags;
    uint model;
    float fadeStart;
    uint2 padding;
    uint4 materials;
};

// A model in the cluster pool: its sphere about its origin, its clusters, its levels (GpuLevel) and its cards
// (GpuCard), the unit-scale distance its cards are drawn from (0 for none), its grid's step, its distance field's slot,
// and how far from its sphere's middle any cluster's error sphere or its parent's reaches, past the sphere, as each
// group's sphere encloses its clusters'.
struct GpuModel
{
    float4 bounds;
    uint firstCluster;
    uint clusterCount;
    uint firstLevel;
    uint levelCount;
    uint firstCard;
    uint cardCount;
    float cardDistance;
    float positionStep;
    uint field;
    float clusterReach;
    uint2 padding;
};

// One level of a model's clusters: where they are, the least error of any of them, and the most error of any of their
// parents', which bound where the level is drawn from.
struct GpuLevel
{
    uint firstCluster;
    uint count;
    float minError;
    float maxParentError;
};

// One cluster: the sphere around it, its group's sphere and error and its parent's (Core's Cluster), where its
// vertices and triangles start in the geometry's words, its counts (triangles in the low 8 bits, vertices in the next
// 9, its material slot in the 3 after, its level in the 5 after) and its model's grid step, which its vertices'
// positions are in.
struct GpuCluster
{
    float4 bounds;
    float4 errorBounds;
    float4 parentErrorBounds;
    float error;
    float parentError;
    uint vertices;
    uint triangles;
    uint counts;
    float step;
    uint2 padding;
};

// One card of a model (Core's Card): its centre, half width (less than 0 when it shows its mesh mirrored), half height
// and turn about +Y, its material slot, and its two atlases' texture refs, TexturePools.None while they load.
struct GpuCard
{
    float3 centre;
    float halfWidth;
    float halfHeight;
    float turn;
    uint slot;
    uint surface;
    uint normals;
    uint3 padding;
};

// A point or spot light in the world's table (the renderer's SceneLights.cs), by its entity's slot: where it is, as
// a float and what it is past that; how far it reaches, its bulb's radius, its intensity in candela times its colour,
// for a spot its axis and the scale and offset of its cone's falloff, and its flags.
static const uint SceneLightLive = 1u;
static const uint SceneLightSpot = 2u;
static const uint SceneLightShadowless = 4u;

struct GpuSceneLight
{
    float3 high;
    float range;
    float3 low;
    float radius;
    float3 intensity;
    float spotScale;
    float3 forward;
    float spotOffset;
    uint flags;
    uint3 padding;
};

// Where a light is relative to the camera.
float3 SceneLightPosition(GpuSceneLight light)
{
    return (light.high - CameraPosition) + (light.low - CameraLow);
}

// Where a scene instance's model-space point is relative to the camera, and was relative to the last frame's.
float3 ScenePosition(GpuSceneInstance instance, float3 position)
{
    float3 turned = float3(dot(instance.row0.xyz, position), dot(instance.row1.xyz, position), dot(instance.row2.xyz, position));
    float3 moved = float3(instance.row0.w, instance.row1.w, instance.row2.w) - CameraPosition;
    return turned + moved + (instance.low - CameraLow);
}

float3 ScenePrevPosition(GpuSceneInstance instance, float3 position)
{
    float3 turned = float3(dot(instance.prevRow0.xyz, position), dot(instance.prevRow1.xyz, position), dot(instance.prevRow2.xyz, position));
    float3 moved = float3(instance.prevRow0.w, instance.prevRow1.w, instance.prevRow2.w) - CameraPosition;
    return turned + moved + (instance.prevLow - CameraLow) + CameraMotion;
}

float3 SceneDirection(GpuSceneInstance instance, float3 direction)
{
    return float3(dot(instance.row0.xyz, direction), dot(instance.row1.xyz, direction), dot(instance.row2.xyz, direction));
}

// As TransformNormal, for a scene instance.
float3 SceneNormal(GpuSceneInstance instance, float3 normal)
{
    float3 cofactor0 = cross(instance.row1.xyz, instance.row2.xyz);
    float3 cofactor1 = cross(instance.row2.xyz, instance.row0.xyz);
    float3 cofactor2 = cross(instance.row0.xyz, instance.row1.xyz);
    float3 turned = float3(dot(cofactor0, normal), dot(cofactor1, normal), dot(cofactor2, normal));
    return NormalizeOrZero(dot(instance.row0.xyz, cofactor0) < 0.0 ? -turned : turned);
}

// A material slot's row, 0xFFFF for none in yet.
uint SceneMaterial(GpuSceneInstance instance, uint slot)
{
    uint word = instance.materials[slot >> 1];
    return (slot & 1u) != 0u ? word >> 16 : word & 0xFFFFu;
}

#endif
