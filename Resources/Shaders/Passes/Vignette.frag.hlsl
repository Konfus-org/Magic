// A sample data pass: darkens the corners of the final image. Reads and writes Ldr, so the renderer
// ping-pongs the target for it.

#include "Include/Pass.hlsli"

struct PassParams
{
    float strength = 0.4;
    float radius = 0.7;
};

float4 main(PassVaryings input) : SV_Target0
{
    PassParams passParams = LoadPassParams();
    float4 color = Ldr.SampleLevel(LdrSampler, input.uv, 0.0);
    float distanceFromCentre = distance(input.uv, float2(0.5, 0.5)) / passParams.radius;
    float falloff = 1.0 - passParams.strength * saturate(distanceFromCentre * distanceFromCentre);
    return float4(color.rgb * falloff, color.a);
}
