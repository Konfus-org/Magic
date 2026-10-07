// The GPU tables only the shaders know the layout of. The rest (GpuInstance and its Instance* flags,
// GpuInstanceXform, GpuPage, GpuCell, GpuLodRow, GpuVisible, GpuDrawArgs, GpuLight, GpuShadowView, GpuShadowHeader,
// GpuBrickJob, GpuCounts, GpuGlow, the Light* tile sizes) are written in front of every shader the renderer
// compiles, from their C# twins marked [Hlsl] in the renderer's Internal/ (GpuStructs.cs, LightTiles.cs): they are
// documented there, and changed there only. Every member here is 16 bytes (or a run of scalars adding up to 16) so
// the layout is the same under D3D packing and DXC's vector-relaxed std430.

#ifndef MAGIC_STRUCTS_HLSLI
#define MAGIC_STRUCTS_HLSLI

// 128 B: a material's parameters, packed by the CPU in the layout the surface shader's MaterialParams
// declares; the generated LoadMaterialParams(slot) unpacks it. Nothing else knows the layout.
struct GpuMaterial
{
    uint4 words[8];
};

static const uint GpuVisibleLevelShift = 28u;
static const uint GpuVisibleSlotMask = (1u << GpuVisibleLevelShift) - 1u;

// 48 B: a local light's hold on its pages between frames: what tells the light apart (its place and reach,
// quantised) and when its faces were last drawn.
struct GpuLocalShadowSlot
{
    float4 key;               // xyz position quantised, w range
    float4 key2;              // xyz direction quantised, w cos(half the outer cone angle)
    uint4 state;              // x used, y faces, z the frame last drawn (ShadowNone never), w the light's row this frame
};

// 128 B, as GiPlan.pass creates it: the GI clipmap as the GPU plans it each frame (Gi/GiPlan.comp.hlsl): how many
// levels of how many voxels, which level is rebuilt this frame, which have been built at least once (valid, bit per
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

#endif
