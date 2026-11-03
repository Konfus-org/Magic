// The HDR picture to the output: scaled by the exposure, bent so the bright keeps its colour, and encoded for the
// screen, until the post passes replace it; or a debug view in its place. The pass header's x is the frame's
// constants, z the lighting constants and w the field constants.
module "WorldRenderer/Tonemap";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Scene.Lighting;
import Scene.Field;
import Trace.DistanceField;

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
    if (lighting.debugView == 1u || lighting.debugView == 2u)
    {
        // The field at the surface, or two voxels above it along the normal: green where it reads the distance to
        // the surface, red where it reads farther out, blue where it reads farther in, the more the farther, in
        // voxels.
        UintTextureRef visibility = { frame.visibility };
        if (visibility.Load(thread.xy, 0u) != 0u)
        {
            TextureRef depthTexture = { frame.depth };
            TextureRef normalTexture = { frame.gbufferNormal };
            float depth = depthTexture.Load(thread.xy, 0u).x;
            float3 world = ReconstructWorld(frame, GetTexelCenterUv(thread.xy, size), depth);
            float3 normal = DecodeOctahedral(normalTexture.Load(thread.xy, 0u).xy * 2.0 - 1.0);
            FieldConstants field = GetFieldConstants();
            uint level;
            SampleField(field, world, level);
            float above = lighting.debugView == 2u ? 2.0 * field.voxelSizes[level] : 0.0;
            float sampled = (SampleField(field, world + normal * above, level) - above) / field.voxelSizes[level];
            float amount = saturate(abs(sampled) / 4.0);
            color = sampled >= 0.0 ? lerp(float3(0.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), amount) : lerp(float3(0.0, 1.0, 0.0), float3(0.0, 0.0, 1.0), amount);
        }
    }

    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(ToSrgb(color), 1.0));
}
