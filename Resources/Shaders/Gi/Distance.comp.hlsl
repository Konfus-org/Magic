// One axis of the level's distance field: the Chebyshev (chessboard) distance to the nearest occupied voxel,
// which is separable. Compiled three times, AXIS 0, 1 and 2: the first reads the level's coverage along X into a
// scratch volume, the next two take the scratch along Y and then Z, the last writing the level's slab of the
// field. One thread per line of voxels along the axis, which it walks in full for every voxel: GiResolution^2
// steps a thread, GiResolution^4 in all, cheaper than a jump flood at this size. Distances are stored as a share
// of GiSdfMaxVoxels, so anything farther reads as that; the chessboard distance is never more than the true one,
// so a sphere trace that steps by it never skips a surface.

#include "Include/Shade.hlsli"
#include "Gi/Common.hlsli"

#ifndef AXIS
#define AXIS 0
#endif

Texture3D<float4> Source : READ(0);
SamplerState SourceSampler : SAMPLER(0);

[[vk::image_format("r8")]]
RWTexture3D<float> Destination : WRITE(0);

// Where voxel k along the axis sits, given the thread's two other coordinates.
uint3 Along(uint2 across, uint k)
{
#if AXIS == 0
    return uint3(k, across.x, across.y);
#elif AXIS == 1
    return uint3(across.x, k, across.y);
#else
    return uint3(across.x, across.y, k);
#endif
}

// The source value at a voxel: the level's slab of the stacked coverage for the first axis, the scratch volume after.
float SourceAt(uint3 voxel)
{
#if AXIS == 0
    float3 uv = GiStackedUv(GiUpdateLevel, (float3)voxel + 0.5);
    return Source.SampleLevel(SourceSampler, uv, 0.0).a >= GiOccupied ? 0.0 : GiSdfMaxVoxels;
#else
    float3 uv = ((float3)voxel + 0.5) / (float)GiResolution;
    return Source.SampleLevel(SourceSampler, uv, 0.0).x * GiSdfMaxVoxels;
#endif
}

[numthreads(8, 8, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 across = threadId.xy;
    if (any(across >= GiResolution))
        return;

    [loop] for (uint k = 0u; k < GiResolution; k++)
    {
        float nearest = GiSdfMaxVoxels;
        [loop] for (uint j = 0u; j < GiResolution; j++)
        {
            float apart = abs((float)k - (float)j);
            if (apart >= nearest)
                continue;

            nearest = min(nearest, max(apart, SourceAt(Along(across, j))));
        }

#if AXIS == 2
        Destination[GiStackedTexel(GiUpdateLevel, Along(across, k))] = nearest / GiSdfMaxVoxels;
#else
        Destination[Along(across, k)] = nearest / GiSdfMaxVoxels;
#endif
    }
}
