// The rows of the world's tables, as the renderer's GpuStructs.cs lays them out member for member: change both
// together. Every member group is 16 bytes. A transform is three float4 rows of transpose(world), applied with dot().
// Each table is read through the heap id the view's constants name (View.hlsli).

#ifndef MAGIC_RENDERING_TABLES_HLSLI
#define MAGIC_RENDERING_TABLES_HLSLI

#include "Include/Math.hlsli"
#include "Rendering/Include/View.hlsli"

// An instance's flags.
static const uint InstanceMirrored = 1u;
static const uint InstanceFadeIn = 2u;

// An instance, by its entity's slot: the rows of transpose(world) (xyz its turn and scale, w its position), and the last
// frame's the same way; its largest scale, its model's row, when its fade started (real seconds) and its eight
// material slots' rows, two to a word.
struct GpuInstance
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 prevRow0;
    float4 prevRow1;
    float4 prevRow2;
    float maxScale;
    uint flags;
    uint model;
    float fadeStartSec;
    uint4 materials;
};

// A model: its sphere about its origin, its clusters, levels and cards, the unit-scale distance its cards are drawn
// from (0 for none), its grid's step, and how far from its sphere's middle any cluster's error sphere or its parent's
// reaches.
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
    float clusterReach;
    uint3 padding;
};

// One level of a model's clusters: where they are, the least error of any of them, and the most of any of their
// parents'.
struct GpuLevel
{
    uint firstCluster;
    uint count;
    float minError;
    float maxParentError;
};

// One cluster: its sphere, its group's sphere and error and its parent's, where its vertices and triangles start in the
// geometry's words, its counts (triangles in the low 8 bits, vertices in the next 9, its material slot in the 3 after,
// its level in the 5 after) and its model's grid step.
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

// One card of a model: its centre, half width (less than 0 when it shows its mesh mirrored), half height and turn about
// +Y, its material slot, and its two atlases' texture ids, TextureNone while they load.
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

// A material's packed params, which its surface's generated LoadMaterialParams unpacks.
struct GpuMaterial
{
    uint4 rows[8];
};

GpuInstance LoadInstance(uint slot)
{
    return BUFFER(View.instances).Load<GpuInstance>(slot * 128u);
}

GpuModel LoadModel(uint row)
{
    return BUFFER(View.models).Load<GpuModel>(row * 64u);
}

GpuLevel LoadLevel(uint row)
{
    return BUFFER(View.levels).Load<GpuLevel>(row * 16u);
}

GpuCluster LoadCluster(uint row)
{
    return BUFFER(View.clusters).Load<GpuCluster>(row * 80u);
}

GpuCard LoadCard(uint row)
{
    return BUFFER(View.cards).Load<GpuCard>(row * 48u);
}

// A material row's word of bins (Culling.hlsli).
uint LoadMaterialBins(uint row)
{
    return BUFFER(View.materialBins).Load(row * 4u);
}

// Where an instance's model-space point is relative to the camera, and was relative to the last frame's.
float3 InstancePosition(GpuInstance instance, float3 position)
{
    float3 turned = float3(dot(instance.row0.xyz, position), dot(instance.row1.xyz, position), dot(instance.row2.xyz, position));
    float3 moved = float3(instance.row0.w, instance.row1.w, instance.row2.w) - View.cameraPosition;
    return turned + moved;
}

float3 InstancePrevPosition(GpuInstance instance, float3 position)
{
    float3 turned = float3(dot(instance.prevRow0.xyz, position), dot(instance.prevRow1.xyz, position), dot(instance.prevRow2.xyz, position));
    float3 moved = float3(instance.prevRow0.w, instance.prevRow1.w, instance.prevRow2.w) - View.cameraPosition;
    return turned + moved + View.cameraMotion;
}

float3 InstanceDirection(GpuInstance instance, float3 direction)
{
    return float3(dot(instance.row0.xyz, direction), dot(instance.row1.xyz, direction), dot(instance.row2.xyz, direction));
}

// A normal through an instance's transform: by its cofactors, so a scale that is not even keeps it at right angles to
// the surface, turned back where the transform mirrors.
float3 InstanceNormal(GpuInstance instance, float3 normal)
{
    float3 cofactor0 = cross(instance.row1.xyz, instance.row2.xyz);
    float3 cofactor1 = cross(instance.row2.xyz, instance.row0.xyz);
    float3 cofactor2 = cross(instance.row0.xyz, instance.row1.xyz);
    float3 turned = float3(dot(cofactor0, normal), dot(cofactor1, normal), dot(cofactor2, normal));
    return NormalizeOrZero(dot(instance.row0.xyz, cofactor0) < 0.0 ? -turned : turned);
}

// A material slot's row.
uint InstanceMaterial(GpuInstance instance, uint slot)
{
    uint word = instance.materials[slot >> 1];
    return (slot & 1u) != 0u ? word >> 16 : word & 0xFFFFu;
}

#endif
