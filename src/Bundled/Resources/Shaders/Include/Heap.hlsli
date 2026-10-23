// The heap: every buffer, texture and sampler the GPU made, which a shader reads by its handle's id, the number
// GpuBuffer, GpuTexture and GpuSampler hold. A shader that reads the heap binds nothing: what it reads arrives as ids in
// its one constant block (CONSTANTS), at most 128 bytes, and its pass lists what it reads and writes
// (GpuComputePass.Reads and Writes), which the GPU waits for. The conventions are Include/Bindings.hlsli's.
//
//   BUFFER(id)              a buffer, byte-addressed, read or written.
//   TEXTURE(id)             a 2D texture, every level, sampled or loaded; UINT_TEXTURE(id) one of integers (r32_uint).
//   TEXTURE_ARRAY(id)       a 2D array texture, every layer; CUBE(id) a cube; VOLUME(id) a 3D texture.
//   LEVEL(id, level)        one level of a 2D texture compute writes, read and written; UINT_LEVEL(id, level) one of
//                           integers, which atomics need; ARRAY_LEVEL(id, level) every layer (or cube face) of one
//                           level of an array or a cube; VOLUME_LEVEL(id, level) one level of a 3D texture.
//   SAMPLER_STATE(id)       a sampler; COMPARE_STATE(id) the same sampler compared, for depth.
//
// Each is indexed non-uniformly, so the id may differ from lane to lane. An id that may be none (TextureNone) is kept
// inside the heap before it indexes, min(id, HEAP_TEXTURES - 1u), even in a branch that skips it: a GPU may fetch the
// descriptor before it knows the branch is not taken. A texture written by a pass is laid out to be written for that
// pass and to be read for a pass that reads it, so one pass never reads and writes the same texture.

#ifndef MAGIC_HEAP_HLSLI
#define MAGIC_HEAP_HLSLI

#include "Include/Bindings.hlsli"

// How many levels each texture has room for in the arrays of levels: a texture's level is at its id times this, plus
// the level. VulkanHeap.LevelsPerTexture.
#define HEAP_LEVELS_PER_TEXTURE 16u

// The most textures the heap holds: VulkanHeap.WantedTextures.
#define HEAP_TEXTURES 8192u

[[vk::binding(0, 0)]] RWByteAddressBuffer HeapBuffers[];
[[vk::binding(1, 0)]] Texture2D HeapTextures[];
[[vk::binding(2, 0)]] Texture2DArray HeapTextureArrays[];
[[vk::binding(3, 0)]] TextureCube HeapCubes[];
[[vk::binding(4, 0)]] Texture3D HeapVolumes[];
[[vk::binding(5, 0)]] Texture2D<uint> HeapUintTextures[];
[[vk::binding(6, 0)]] [[vk::image_format("unknown")]] RWTexture2D<float4> HeapLevels[];
[[vk::binding(7, 0)]] [[vk::image_format("unknown")]] RWTexture2DArray<float4> HeapArrayLevels[];
[[vk::binding(8, 0)]] [[vk::image_format("unknown")]] RWTexture3D<float4> HeapVolumeLevels[];
[[vk::binding(9, 0)]] [[vk::image_format("r32ui")]] RWTexture2D<uint> HeapUintLevels[];
[[vk::binding(10, 0)]] SamplerState HeapSamplers[];
[[vk::binding(11, 0)]] SamplerComparisonState HeapCompareSamplers[];

#define CONSTANTS(type, name) [[vk::push_constant]] type name

#define BUFFER(id) HeapBuffers[NonUniformResourceIndex(id)]
#define TEXTURE(id) HeapTextures[NonUniformResourceIndex(id)]
#define TEXTURE_ARRAY(id) HeapTextureArrays[NonUniformResourceIndex(id)]
#define CUBE(id) HeapCubes[NonUniformResourceIndex(id)]
#define VOLUME(id) HeapVolumes[NonUniformResourceIndex(id)]
#define UINT_TEXTURE(id) HeapUintTextures[NonUniformResourceIndex(id)]
#define LEVEL(id, level) HeapLevels[NonUniformResourceIndex((id) * HEAP_LEVELS_PER_TEXTURE + (level))]
#define ARRAY_LEVEL(id, level) HeapArrayLevels[NonUniformResourceIndex((id) * HEAP_LEVELS_PER_TEXTURE + (level))]
#define VOLUME_LEVEL(id, level) HeapVolumeLevels[NonUniformResourceIndex((id) * HEAP_LEVELS_PER_TEXTURE + (level))]
#define UINT_LEVEL(id, level) HeapUintLevels[NonUniformResourceIndex((id) * HEAP_LEVELS_PER_TEXTURE + (level))]
#define SAMPLER_STATE(id) HeapSamplers[NonUniformResourceIndex(id)]
#define COMPARE_STATE(id) HeapCompareSamplers[NonUniformResourceIndex(id)]

// The size of a 2D texture's level, in texels.
uint2 SizeOf(Texture2D texture, uint level)
{
    uint width;
    uint height;
    uint levels;
    texture.GetDimensions(level, width, height, levels);
    return uint2(width, height);
}

uint2 SizeOf(RWTexture2D<float4> texture)
{
    uint width;
    uint height;
    texture.GetDimensions(width, height);
    return uint2(width, height);
}

#endif
