// The HDR picture to the output: scaled by the exposure, bent so the bright keeps its colour, and encoded for the
// screen, until the post passes replace it. The pass header's x is the frame's constants and z the lighting
// constants.
module "WorldRenderer/Tonemap";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Color;
import Scene.Lighting;

[shader("compute")]
[numthreads(8, 8, 1)]
void Tonemap(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    TextureRef hdr = { lighting.hdr };
    float3 color = max(hdr.Load(thread.xy, 0u).rgb * lighting.exposure, 0.0);
    color = color / (1.0 + color);
    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(ToSrgb(color), 1.0));
}
