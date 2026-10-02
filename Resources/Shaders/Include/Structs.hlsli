// The GPU tables, mirroring Magic/Contexts/Rendering/GpuStructs.cs field for field. Every member is 16 bytes
// (or a run of scalars adding up to 16) so the layout is the same under D3D packing and DXC's vector-relaxed
// std430. Never change a struct here without its C# twin (or the other way round): both sides read the same
// bytes blind, so a mismatch compiles and draws garbage.

#ifndef MAGIC_STRUCTS_HLSLI
#define MAGIC_STRUCTS_HLSLI

// 32 B: one drawn instance. sphere is its bounds in absolute world space; bucketGroup indexes the draw-args
// slot of its (pipeline class, mesh) pair. meshSlot is the CPU's bookkeeping, no shader reads it.
struct GpuInstance
{
    float4 sphere;
    uint meshSlot;
    uint materialSlot;
    uint bucketGroup;
    uint flags;
};

// GpuInstance.flags
static const uint InstanceAlive = 1u << 0;
static const uint InstanceMirrored = 1u << 1;
static const uint InstanceNoSizeCull = 1u << 2;

// 48 B: the three rows of transpose(world), so p' = (dot(r0, p), dot(r1, p), dot(r2, p)) with p.w = 1.
struct GpuInstanceXform
{
    float4 r0;
    float4 r1;
    float4 r2;
};

// 128 B: a material's parameters, packed by the CPU in the layout the surface shader's MaterialParams
// declares; the generated LoadMaterialParams(slot) unpacks it. Nothing else knows the layout.
struct GpuMaterial
{
    uint4 words[8];
};

// 16 B: a run of instance slots that belong to one cell.
struct GpuPage
{
    uint firstInstance;
    uint count;
    uint cell;
    uint pad;
};

// 32 B: a residency cell's world-space bounds, w unused.
struct GpuCell
{
    float4 aabbMin;
    float4 aabbMax;
};

// 20 B: SDL's GPUIndexedIndirectDrawCommand, one per bucket group. The field order is the graphics API's
// (the GPU reads it as the draw), not ours: never reorder it, here or in C# DrawArgs.
struct GpuDrawArgs
{
    uint indexCount;
    uint instanceCount;
    uint firstIndex;
    int vertexOffset;
    uint firstInstance;
};

#endif
