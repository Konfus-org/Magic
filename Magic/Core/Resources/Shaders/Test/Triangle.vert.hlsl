// Conventions probe: two triangles straight from a vertex buffer in NDC. The pass draws them with
// FrontFace = Clockwise and CullMode = Back, so only the one wound clockwise on screen survives. VsOut must
// match PsIn in Triangle.frag.hlsl.

struct VsIn
{
    float4 position : TEXCOORD0; // xyz in NDC, w unused
    float4 color : TEXCOORD1;
};

struct VsOut
{
    float4 color : TEXCOORD0;
    float4 position : SV_Position;
};

VsOut main(VsIn input)
{
    VsOut output;
    output.position = float4(input.position.xyz, 1.0);
    output.color = input.color;
    return output;
}
