// The GBuffer lit by the sun alone, shadowed by its pages, with the sky as a flat ambient from above, and the sky
// itself where nothing was drawn; then a plain tonemap into the output, until the lighting and post passes replace
// it. The pass header's x is the frame's constants and y the shadow constants.
module "WorldRenderer/ShadeFlat";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Shadow.Virtual;

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeFlat(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    UintTextureRef visibility = { frame.visibility };
    float3 color;
    if (visibility.Load(thread.xy, 0u) == 0u)
    {
        // The sky: brighter towards the zenith, as the pixel's row stands in for the view's elevation.
        float up = 1.0 - float(thread.y) / float(size.y);
        color = frame.skyRadiance.rgb * lerp(0.6, 1.4, up);
    }
    else
    {
        TextureRef normalTexture = { frame.gbufferNormal };
        TextureRef baseTexture = { frame.gbufferBase };
        TextureRef depthTexture = { frame.depth };
        float3 normal = DecodeOctahedral(normalTexture.Load(thread.xy, 0u).xy * 2.0 - 1.0);
        float4 base = baseTexture.Load(thread.xy, 0u);
        float depth = depthTexture.Load(thread.xy, 0u).x;
        float3 world = ReconstructWorld(frame, GetTexelCenterUv(thread.xy, size), depth);
        ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
        float visible = SampleSunShadow(shadow, world, normal, ToViewDepth(depth, frame.near));
        float3 diffuse = base.rgb * (1.0 - base.a);
        float sun = saturate(dot(normal, frame.sunDirection.xyz)) * visible;
        float sky = 0.5 + 0.5 * normal.y;
        color = diffuse * (frame.sunRadiance.rgb * sun + frame.skyRadiance.rgb * sky);
    }

    color = color / (1.0 + color);
    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(ToSrgb(color), 1.0));
}
