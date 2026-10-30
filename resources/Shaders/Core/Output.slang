// An image shown in its target, a fullscreen pass (Core/Pass's DrawFullscreen, then ShowImage): a view's finished
// image, or the one kept as the world loaded, scaled into its rectangle, with the words of what failed this frame over
// it (Core/Failure), encoded and dithered as the target's image expects (Core/Color's EncodeForTarget). The pass
// header's frame alone is read, for its clocks: the banner pulses by its real time, and its index dithers.

module "Core/Output";

import Core.Color;
import Core.Failure;
import Core.Pass;

// The renderer's Output's constants, member for member.
struct OutputConstants
{
    TextureRef image;        // the image shown
    SamplerRef sampler;      // what it is scaled through
    ArrayRef<uint> text;     // the failure banner's glyphs
    ArrayRef<uint> failures; // the frame's raised codes
    float2 size;             // the rectangle it is shown in, in pixels
    uint encoding;           // the target's TargetEncoding
}

[shader("fragment")]
float4 ShowImage(FullscreenVaryings input, uniform PassHeader pass, uniform OutputConstants c) : SV_Target0
{
    FrameConstants clocks = pass.clocks;
    TargetEncoding encoding = (TargetEncoding)c.encoding;
    float3 color = c.image.SampleLevel(c.sampler, input.uv, 0.0).rgb;
    color = ApplyFailureBanner(color, uint2(input.uv * c.size), uint2(c.size), c.text, c.failures, clocks.realSec);
    float3 encoded = EncodeForTarget(color, encoding) + GetTargetDither(encoding, input.position.xy, clocks.index);
    return float4(encoded, 1.0);
}
