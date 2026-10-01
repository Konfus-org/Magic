// Conventions probe: proves the CPU/GPU matrix agreement on this backend. Output[0] = mul(M, v) for an
// untransposed System.Numerics matrix in a cbuffer, which must equal Vector4.Transform(v, M); Output[1] =
// the 3x4 storage-buffer form (three float4 rows of transpose(world)) applied with dot(), which must equal
// Vector4.Transform(v, world).xyz. The renderer reads both back (RenderChecks.cs).

#include "Include/Bindings.hlsli"

struct Probe
{
    float4 value;
    float4 r0;
    float4 r1;
    float4 r2;
};

cbuffer Constants : UNIFORM(0)
{
    float4x4 Matrix;
};

StructuredBuffer<Probe> Input : READ(0);
RWStructuredBuffer<float4> Output : WRITE(0);

[numthreads(1, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    Probe probe = Input[0];
    Output[0] = mul(Matrix, probe.value);
    Output[1] = float4(dot(probe.r0, probe.value), dot(probe.r1, probe.value), dot(probe.r2, probe.value), 1.0);
}
