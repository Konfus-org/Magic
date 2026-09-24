// M1 conventions probe: proves the CPU/GPU matrix agreement on both backends before any other shader is
// written. Output[0] = mul(M, v) for an untransposed System.Numerics matrix in a cbuffer, which must equal
// Vector4.Transform(v, M); Output[1] = the 3x4 storage-buffer form (three float4 rows of transpose(world))
// applied with dot(), which must equal Vector4.Transform(v, world).xyz. The renderer reads both back.
#include "Include/Bindings.hlsli"

struct Probe
{
    float4 v;
    float4 r0;
    float4 r1;
    float4 r2;
};

cbuffer Constants : CS_CB(0)
{
    float4x4 M;
};

StructuredBuffer<Probe> Input : CS_ROBUF(0);
RWStructuredBuffer<float4> Output : CS_RWBUF(0);

[numthreads(1, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    Probe p = Input[0];
    Output[0] = mul(M, p.v);
    Output[1] = float4(dot(p.r0, p.v), dot(p.r1, p.v), dot(p.r2, p.v), 1.0);
}
