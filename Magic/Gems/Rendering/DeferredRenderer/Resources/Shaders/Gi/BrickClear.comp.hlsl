// Empties one brick of the occupancy atlas before its mesh is rasterised into it. One thread per cell, one run of
// the pass per brick job this frame.

#include "Include/Frame.hlsli"
#include "Gi/Common.hlsli"

StructuredBuffer<GpuBrickJob> BrickJobs : READ(0);
[[vk::image_format("r8")]]
RWTexture3D<float> BrickAtlas : WRITE(0);

#include "Gi/Brick.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint cell = threadId.x;
    if (cell >= GI_BRICK * GI_BRICK * GI_BRICK)
        return;

    GpuBrickJob job = BrickJobOfPass();
    uint3 inBrick = uint3(cell % GI_BRICK, (cell / GI_BRICK) % GI_BRICK, cell / (GI_BRICK * GI_BRICK));
    BrickAtlas[GiBrickOffset(job.brick) + inBrick] = 0.0;
}
