// Conventions probe: the interpolated vertex colour, written as linear into the sRGB colour target. PsIn
// must match VsOut in Triangle.vert.hlsl.

struct PsIn
{
    float4 color : TEXCOORD0;
};

float4 main(PsIn input) : SV_Target0
{
    return input.color;
}
