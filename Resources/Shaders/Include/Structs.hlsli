// The GPU tables, mirroring Magic/Contexts/Rendering/GpuStructs.cs field for field. Every member is 16 bytes
// (or a run of scalars adding up to 16) so the layout is the same under D3D packing and DXC's vector-relaxed
// std430. Never change a struct here without its C# twin (or the other way round): both sides read the same
// bytes blind, so a mismatch compiles and draws garbage.

#ifndef MAGIC_STRUCTS_HLSLI
#define MAGIC_STRUCTS_HLSLI

// 32 B: one drawn instance. sphere is its bounds in absolute world space; bucketGroup indexes the draw-args
// slot of its (pipeline class, mesh) pair. cullRadius is the world-space radius the size cull judges it by:
// that of its sphere, unless it is many small things in one and says how small.
struct GpuInstance
{
    float4 sphere;
    float cullRadius;
    uint materialSlot;
    uint bucketGroup;
    uint flags;
};

// GpuInstance.flags
static const uint InstanceAlive = 1u << 0;
static const uint InstanceMirrored = 1u << 1;
static const uint InstanceNoSizeCull = 1u << 2;
static const uint InstanceHidden = 1u << 3; // registered, not drawn

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

// 32 B: the lesser versions (LODs) of one bucket group's mesh, one row per group. An instance of the group is
// drawn in group1 once its height on screen, as a fraction of the view's, is under thresholds.x, in group2
// under thresholds.y, in group3 under thresholds.z; count is how many of the three there are.
struct GpuLodRow
{
    float4 thresholds;
    uint group1;
    uint group2;
    uint group3;
    uint count;
};

// 8 B: one entry of a view's visible list, read by the vertex stage through the instance-rate buffer: the
// instance's slot, and how much of it is drawn while it blends between two versions of its mesh. lodFade 1 is
// all of it; f in (0, 1) keeps the pixels whose dither value is under f; -f keeps the others, so the two
// versions of one instance, drawn with f and -f, cover every pixel exactly once.
struct GpuVisible
{
    uint slot;
    float lodFade;
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

// 48 B: one point or spot light, in absolute world space. A point light is a spot whose cone never ends: its
// cosines are below any a direction can have, so nothing branches on the kind.
struct GpuLight
{
    float4 positionRange;     // xyz position, w range in metres: nothing farther is lit
    float4 colorInnerCos;     // rgb linear colour times intensity, w cos(half the inner cone angle)
    float4 directionOuterCos; // xyz the direction the light travels, w cos(half the outer cone angle)
};

// The lights of a view are binned in two steps. First into screen tiles of LightTileSize pixels a side
// (Lighting/LightCull.comp.hlsl), one row of LightTileWords uints per tile, left to right then top to
// bottom: how many lights reach what was drawn in the tile, the nearest and the farthest view depth drawn
// there (the bits of two floats), then the lights' rows in the lights buffer. Then every tile is cut along
// its depth into LightSlices clusters (Lighting/LightCluster.comp.hlsl), LightClusterWords uints each,
// slice after slice behind their tile: the count, then the lights of the tile that reach that slice. A pixel
// is lit by the lights of its cluster. A row more lights reach than fit keeps the first ones and its count
// says how many reach it, so the lighting pass shows it as a failure.
//
// A tile's slices are even steps of the logarithm of the view depth between the nearest and the farthest
// depth drawn in that tile, so every tile spends all its slices on the stretch it actually shows: a tile
// looking along the ground to the horizon cuts those hundreds of metres into LightSlices pieces. Twins:
// Lighting.TileSize, TileWords, Slices and ClusterWords.
static const uint LightTileSize = 32u;
static const uint LightTileWords = 1024u;
static const uint LightTileHeader = 3u;
static const uint LightsPerTile = LightTileWords - LightTileHeader;
static const uint LightSlices = 16u;
static const uint LightClusterWords = 64u;
static const uint LightsPerCluster = LightClusterWords - 1u;

// 32 B: one glow, an unlit bright dot (Lighting/Glow.vert.hlsl), in absolute world space.
struct GpuGlow
{
    float4 positionRadius; // xyz position, w radius in metres
    float4 color;          // rgb linear, emitted as it is; a unused
};

#endif
