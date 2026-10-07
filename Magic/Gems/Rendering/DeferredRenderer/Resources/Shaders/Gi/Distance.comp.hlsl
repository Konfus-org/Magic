// One axis of the level's distance field: the Chebyshev (chessboard) distance to the nearest occupied voxel,
// which is separable. Compiled three times, AXIS 0, 1 and 2: the first reads the level's coverage along X into a
// scratch volume, the next two take the scratch along Y and then Z, the last writing the level's slab of the
// field. One thread per line of voxels along the axis: it reads its line once into group memory, then for every
// voxel looks only as far as a distance can reach (GiSdfMaxVoxels either way), since anything farther reads as
// that anyway. Distances are stored as a share of GiSdfMaxVoxels; the chessboard distance is never more than the
// true one, so a sphere trace that steps by it never skips a surface.

#include "Include/Frame.hlsli"
#include "Gi/Common.hlsli"

#ifndef AXIS
#define AXIS 0
#endif

Texture3D<float4> Source : READ(0);
SamplerState SourceSampler : SAMPLER(0);

[[vk::image_format("r8")]]
RWTexture3D<float> Destination : WRITE(0);
StructuredBuffer<GpuGiState> GiState : READ(1);

#include "Gi/State.hlsli"

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
    float3 uv = GiStackedUv(GiUpdateLevel(), (float3)voxel + 0.5);
    return Source.SampleLevel(SourceSampler, uv, 0.0).a >= GiOccupied ? 0.0 : GiSdfMaxVoxels;
#else
    float3 uv = ((float3)voxel + 0.5) / (float)GiResolution();
    return Source.SampleLevel(SourceSampler, uv, 0.0).x * GiSdfMaxVoxels;
#endif
}

#define GROUP_LINES 32      // 8 x 4 lines a group: 16 KB of group memory at the most resolution
#define MAX_RESOLUTION 128  // twin: the GI plan pass's resolution, at most this
static const int Reach = (int)GiSdfMaxVoxels;

groupshared float Line[GROUP_LINES][MAX_RESOLUTION];

[numthreads(8, 4, 1)]
void main(uint3 threadId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint2 across = threadId.xy;
    if (!GiRebuilding() || any(across >= GiResolution()))
        return;

    int resolution = (int)min(GiResolution(), (uint)MAX_RESOLUTION);
    [loop] for (int read = 0; read < resolution; read++)
        Line[groupIndex][read] = SourceAt(Along(across, (uint)read));

    // Only this thread reads its own line: no barrier needed.
    [loop] for (uint k = 0u; k < (uint)resolution; k++)
    {
        float nearest = GiSdfMaxVoxels;
        int first = max((int)k - Reach + 1, 0);
        int last = min((int)k + Reach - 1, resolution - 1);
        [loop] for (int j = first; j <= last; j++)
            nearest = min(nearest, max(abs((float)k - (float)j), Line[groupIndex][j]));

#if AXIS == 2
        Destination[GiStackedTexel(GiUpdateLevel(), Along(across, k))] = nearest / GiSdfMaxVoxels;
#else
        Destination[Along(across, k)] = nearest / GiSdfMaxVoxels;
#endif
    }
}
