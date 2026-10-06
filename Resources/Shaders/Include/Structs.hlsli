// The GPU tables, mirroring Gems/DeferredRenderer/GpuStructs.cs field for field. Every member is 16 bytes
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
static const uint InstanceNoShadow = 1u << 4; // drawn, but into no shadow map

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
// drawn in groups[i] once its height on screen, as a fraction of the view's, is under thresholds[i], the last
// such i; the thresholds fall from x to w, and a level whose threshold is 0 is not there (a mesh's impostor is
// the last level it has).
struct GpuLodRow
{
    float4 thresholds;
    uint4 groups;
};

// 8 B: one entry of a view's visible list, read by the vertex stage through the instance-rate buffer: the
// instance's slot, and how much of it is drawn while it blends between two versions of its mesh. lodFade 1 is
// all of it; f in (0, 1) keeps the pixels whose dither value is under f; -f keeps the others, so the two
// versions of one instance, drawn with f and -f, cover every pixel exactly once. The slot's top bits are the level
// of detail it is drawn at (0 the full mesh), for the debug view: mask with GpuVisibleSlotMask before indexing.
struct GpuVisible
{
    uint slot;
    float lodFade;
};

static const uint GpuVisibleLevelShift = 28u;
static const uint GpuVisibleSlotMask = (1u << GpuVisibleLevelShift) - 1u;

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

// 80 B: one point, spot or area light, in absolute world space. A point light is a spot whose cone never ends: its
// cosines are below any a direction can have, so nothing branches on the kind. An area light is a spot whose cone is
// its front half, falling off with the cosine (inner 1, outer 0), seen from the part of its rectangle a surface looks
// at (Lighting/Common.hlsli); its range is grown by half the rectangle's diagonal, so a reach measured from its
// middle holds it all. shadow.x is the row of its first
// shadow view (GpuShadowView), ShadowNone when it holds no pages this frame; shadow.y how many faces it has (1 a
// spot, 6 a point); shadow.z bit 0 whether it may cast at all. The CPU uploads x and y as none; the GPU's local
// shadow selection fills them in.
struct GpuLight
{
    float4 positionRange;     // xyz position, w range in metres: nothing farther is lit
    float4 colorInnerCos;     // rgb linear colour times intensity, w cos(half the inner cone angle)
    float4 directionOuterCos; // xyz the direction the light travels, w cos(half the outer cone angle)
    float4 area;              // an area light's xyz +X edge direction times half its width, w half its height; else 0
    uint4 shadow;             // x first shadow view row or none, y face count, z LightCastsShadows, w unused
};

// 400 B: one shadow view, a cascade of the sun or a face of a local light, as the GPU planned it
// (Include/ShadowViews.hlsli): its tile of the atlas, its matrices relative to its own eye (a position relative
// to the camera is moved by CameraPos - eye first), and the planes of the receivers' volume the casters are
// culled against (none for a face). flags.y set means the tile is drawn again this frame; flags.w is the slice
// of the visible-id buffer its draw reads from then.
struct GpuShadowView
{
    float4x4 viewProj;        // eye-relative world to the tile's clip space, reverse-Z
    float4x4 rotation;        // the view's rotation: rows right, up, forward
    float4 eye;               // xyz the eye, absolute; w metres per texel (orthographic) or per texel per metre from the eye (perspective)
    float4 tileUv;            // xy where the tile starts in the atlas, zw its size, in uv
    float4 tileTexels;        // the same in texels
    float4 range;             // x the view depth a cascade ends at (a face: the light's range), y near plane, z far plane, w tan(half the fov) for a face
    uint4 flags;              // x ShadowView* bits, y refreshed this frame, z receiver plane count, w visible-id slice
    float4 planes[12];        // the receivers' swept volume, relative to eye, normals pointing in
};

// 64 B: what the shadow planning passes tell the rest of the frame.
struct GpuShadowHeader
{
    uint cascadeCount;
    uint flags;               // Shadow*Flag bits
    uint refreshedCount;      // rows drawn again this frame, listed in the refreshed buffer
    uint generation;          // a hash of the layout: when it changes every tile is drawn again
    float4 atlasTexel;        // xy one texel in uv, zw a cascade tile's size in uv
    uint4 layout;             // x atlas width, y height, z cascade resolution, w page size, all in texels
    uint4 slots;              // x local light slots, y rows in use, zw unused
};

// 48 B: a local light's hold on its pages between frames: what tells the light apart (its place and reach,
// quantised) and when its faces were last drawn.
struct GpuLocalShadowSlot
{
    float4 key;               // xyz position quantised, w range
    float4 key2;              // xyz direction quantised, w cos(half the outer cone angle)
    uint4 state;              // x used, y faces, z the frame last drawn (ShadowNone never), w the light's row this frame
};

// 128 B: the GI clipmap as the GPU plans it each frame (Gi/GiPlan.comp.hlsl, twin: GpuGiState): how many levels of
// how many voxels, which level is rebuilt this frame, which have been built at least once (valid, bit per
// level; previousValid as it was before this frame), where each level sits (origins: xyz the min corner,
// absolute, w the voxel size), how many voxels the rebuilt level's origin moved (shift), and which of the two
// light sets holds each level's current light (parity, one word per level).
struct GpuGiState
{
    uint levels;
    uint resolution;
    uint updateLevel;
    uint valid;
    float4 origins[4];
    float4 shift;
    uint4 parity;
    uint4 extra;              // x the layout's generation, y previousValid, zw unused
};

// 48 B: one occupancy brick to build (twin: GpuBrickJob): the mesh's box in its own space, where its triangles sit
// in the mega buffers, and the brick of the atlas it fills.
struct GpuBrickJob
{
    float4 boxMin;
    float4 boxMax;
    uint firstIndex;
    uint indexCount;
    int vertexOffset;
    uint brick;
};

// 32 B: this frame's counts, uploaded by the CPU (twin: GpuCounts), so a pass sizes its work by them.
struct GpuCounts
{
    uint pageCount;
    uint chunkCount;
    uint lightCount;
    uint visibleHighWater;
    uint glowCount;
    uint frameIndex;
    uint groupCount;
    uint brickJobCount;
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
