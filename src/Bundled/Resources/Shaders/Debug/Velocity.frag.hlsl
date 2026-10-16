// Where each pixel's surface was last frame, as colour: grey still, red and green its motion across the view, a
// thousand pixels a second saturating.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    float2 velocity = Velocity.Load(int3(pixel, 0));
    return float4(saturate(0.5 + velocity * ViewSize / max(DeltaSeconds, 1e-4) / 2000.0), 0.5, 1.0);
}
