// One cell of a capture written into its atlases: the cell's visibility buffer, GBuffer and depth, drawn from one
// direction with the model at the origin, copied to the cell's place, covered where a pixel holds a surface. The
// pass header's y is the cell and z the capture row.
module "WorldRenderer/CaptureCell";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Captures;

[shader("compute")]
[numthreads(8, 8, 1)]
void CaptureCell(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    if (any(thread.xy >= uint2(CaptureCellSize, CaptureCellSize)))
        return;

    uint2 cell = uint2(header.y % CaptureCells, header.y / CaptureCells);
    uint2 texel = (cell * CaptureCellSize) + thread.xy;
    GpuCapture capture = LoadCapture(frame.captures, header.z);
    StorageRef albedoAtlas = { capture.albedo, 0u };
    StorageRef normalAtlas = { capture.normal, 0u };
    StorageRef depthAtlas = { capture.depth, 0u };

    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(thread.xy, 0u) == 0u)
    {
        albedoAtlas.Store(texel, float4(0.0));
        normalAtlas.Store(texel, float4(0.5, 0.5, 0.0, 0.0));
        depthAtlas.Store(texel, float4(0.0));
        return;
    }

    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef depthTexture = { frame.depth };
    albedoAtlas.Store(texel, float4(baseTexture.Load(thread.xy, 0u).rgb, 1.0));
    normalAtlas.Store(texel, float4(normalTexture.Load(thread.xy, 0u).xy, 0.0, 0.0));
    depthAtlas.Store(texel, float4(depthTexture.Load(thread.xy, 0u).x, 0.0, 0.0, 0.0));
}
