// A run of per-pixel post effects as one pass, a thread a pixel: each pixel of the image through each effect in turn
// (Core/Post's IPixelEffect), E being the run as the renderer names it, Then<A, Then<B, C>> for three, its values packed
// after the header and the images as the run lays them out.

module "Core/PixelEffects";

__exported import Core.Post;

[shader("compute")]
[numthreads(8, 8, 1)]
void ApplyPixelEffects<E : IPixelEffect>(
    uint3 id : SV_DispatchThreadID,
    uniform PassHeader pass,
    uniform PostImages images,
    uniform E effects)
{
    LevelRef output = images.output.GetLevel(0u);
    uint2 size = output.size;
    if (any(id.xy >= size))
    {
        return;
    }

    PixelInputs input;
    input.pixel = id.xy;
    input.uv = (float2(id.xy) + 0.5) / float2(size);
    input.frame = pass.clocks;
    float3 color = images.image.Load(int2(id.xy), 0).rgb;
    output[id.xy] = float4(effects.Apply(color, input), 1.0);
}
