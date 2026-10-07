// The engine's tonemap: exposure, an ACES-fitted curve, then the sRGB colour target applies the OETF. The
// only place linear light becomes display light.

#include "Include/Pass.hlsli"

struct PassParams
{
    float exposure = 1.0;
};

// Narkowicz's fit of the ACES filmic curve.
float3 Aces(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

float4 main(PassVaryings input) : SV_Target0
{
    PassParams passParams = LoadPassParams();
    float3 hdr = Hdr.SampleLevel(HdrSampler, input.uv, 0.0).rgb * passParams.exposure;
    return float4(Aces(hdr), 1.0);
}
