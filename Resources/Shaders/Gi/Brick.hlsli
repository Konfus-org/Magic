// The constants of one brick job (Gi/BrickClear.comp.hlsl, Gi/BrickBuild.comp.hlsl): the frame block, then the
// mesh's box and its triangles' place in the mega buffers, as BrickConstants in Gems/DeferredRenderer/GpuStructs.cs
// lays them out.

#ifndef MAGIC_GI_BRICK_HLSLI
#define MAGIC_GI_BRICK_HLSLI

#define FRAME_APPEND \
    float4 BrickBoxMin; \
    float4 BrickBoxMax; \
    uint4 BrickJob; /* x first index, y index count, z vertex offset, w the brick */
#include "Include/Frame.hlsli"

#endif
