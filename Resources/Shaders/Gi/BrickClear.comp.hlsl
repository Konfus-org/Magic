// Empties one brick of the occupancy atlas before its mesh is rasterised into it. One thread per cell.

#include "Gi/Brick.hlsli"
#include "Gi/Common.hlsli"

[[vk::image_format("r8")]]
RWTexture3D<float> BrickAtlas : WRITE(0);

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint cell = threadId.x;
    if (cell >= GI_BRICK * GI_BRICK * GI_BRICK)
        return;

    uint3 inBrick = uint3(cell % GI_BRICK, (cell / GI_BRICK) % GI_BRICK, cell / (GI_BRICK * GI_BRICK));
    BrickAtlas[GiBrickOffset(BrickJob.w) + inBrick] = 0.0;
}
