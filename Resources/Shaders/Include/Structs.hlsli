// The GPU tables, mirroring Gems/Render/Residency/GpuStructs.cs field for field. Every member is 16 bytes so
// the layout is the same under D3D packing and DXC's vector-relaxed std430: a float3 followed by a scalar is
// the one thing that would differ, so there are none. Scalars that are not floats travel as float bits
// (asfloat on the CPU, asuint here).
#ifndef MAGIC_STRUCTS_HLSLI
#define MAGIC_STRUCTS_HLSLI

struct GpuLod
{
    uint firstIndex;
    uint indexCount;
    float error;
    uint pad;
};

// 112 B. aabbMinLodCount.w = asfloat(lodCount), aabbMaxVertexOffset.w = asfloat(vertexOffset).
struct GpuMesh
{
    float4 sphere;
    float4 aabbMinLodCount;
    float4 aabbMaxVertexOffset;
    GpuLod lods[4];
};

// 32 B. sphere is the instance's bounds in absolute world space.
struct GpuInstanceCull
{
    float4 sphere;
    uint meshSlot;
    uint materialSlot;
    uint flags;
    uint cell;
};

// 48 B: the three rows of transpose(world), so p' = (dot(r0, p), dot(r1, p), dot(r2, p)) with p.w = 1.
struct GpuInstanceXform
{
    float4 r0;
    float4 r1;
    float4 r2;
};

// 64 B. textures = (baseColor, normal, orm, emissive) as (poolSlot << 16) | layer, TEXTURE_NONE when unset.
// roughMetalNormalFlags.w = asfloat(MaterialFlags).
struct GpuMaterial
{
    uint4 textures;
    float4 baseColor;
    float4 emissiveAlphaCutoff;
    float4 roughMetalNormalFlags;
};

#define TEXTURE_NONE 0xFFFFFFFF

// GpuInstanceCull.flags
#define INSTANCE_ALIVE        (1u << 0)
#define INSTANCE_CASTS_SHADOW (1u << 1)
#define INSTANCE_MIRRORED     (1u << 2)
#define INSTANCE_DOUBLE_SIDED (1u << 3)
#define INSTANCE_MASKED       (1u << 4)
#define INSTANCE_NO_SIZE_CULL (1u << 5)
#define INSTANCE_DYNAMIC      (1u << 6)
#define INSTANCE_GI_ELIGIBLE  (1u << 7)

// GpuMaterial.roughMetalNormalFlags.w, as Magic.Contexts.Assets.MaterialFlags
#define MATERIAL_DOUBLE_SIDED 1u
#define MATERIAL_MASKED       2u
#define MATERIAL_CLAMP_UV     4u

#endif
