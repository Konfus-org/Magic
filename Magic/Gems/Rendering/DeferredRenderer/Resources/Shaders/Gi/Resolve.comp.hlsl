// One thread per voxel of the level being rebuilt: what was accumulated into it becomes its albedo and coverage and
// its emission, in the level's slab of the stacked volumes, and the accumulator is zeroed for the next level. The
// light grid's counts are zeroed here too, before LightGrid.comp.hlsl fills them.

#include "Include/Frame.hlsli"
#include "Gi/Common.hlsli"

[[vk::image_format("rgba8")]]
RWTexture3D<float4> Albedo : WRITE(0);
[[vk::image_format("rgba16f")]]
RWTexture3D<float4> Emissive : WRITE(1);
RWStructuredBuffer<uint> Accum : WRITE(2);
RWStructuredBuffer<uint> LightGrid : WRITE(3);
StructuredBuffer<GpuGiState> GiState : READ(0);

#include "Gi/State.hlsli"

[numthreads(4, 4, 4)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (!GiRebuilding() || any(threadId >= GiResolution()))
        return;

    uint index = GiVoxelIndex(threadId);
    uint row = index * GI_ACCUM_WORDS;
    float coverage;
    float3 albedo;
    GiUnpackAccum(Accum[row], Accum[row + 1u], Accum[row + 2u], Accum[row + 3u], coverage, albedo);
    float3 emissive = float3(asfloat(Accum[row + 4u]), asfloat(Accum[row + 5u]), asfloat(Accum[row + 6u]));

    int3 texel = GiStackedTexel(GiUpdateLevel(), threadId);
    Albedo[texel] = float4(albedo, coverage);
    Emissive[texel] = float4(emissive, 0.0);

    [unroll] for (uint word = 0u; word < GI_ACCUM_WORDS; word++)
        Accum[row + word] = 0u;

    if (index < GI_LIGHT_CELLS * GI_LIGHT_CELLS * GI_LIGHT_CELLS)
        LightGrid[index * (GI_LIGHTS_PER_CELL + 1u)] = 0u;
}
