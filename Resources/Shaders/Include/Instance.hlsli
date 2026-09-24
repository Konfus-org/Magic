// Instance transforms as the culler and the vertex shaders read them: three float4 rows of transpose(world)
// per instance (see Structs.hlsli), applied with dot() because storage buffers carry no matrix types. The
// buffers default to the vertex stage's first two storage slots; a compute shader defines the registers
// before including this file.
#ifndef MAGIC_INSTANCE_HLSLI
#define MAGIC_INSTANCE_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Structs.hlsli"

#ifndef INSTANCE_XFORM_REGISTER
#define INSTANCE_XFORM_REGISTER VS_BUF(0)
#endif
#ifndef INSTANCE_CULL_REGISTER
#define INSTANCE_CULL_REGISTER VS_BUF(1)
#endif

StructuredBuffer<GpuInstanceXform> InstanceXform : INSTANCE_XFORM_REGISTER;
StructuredBuffer<GpuInstanceCull> InstanceCull : INSTANCE_CULL_REGISTER;

GpuInstanceXform LoadWorld(uint instance)
{
    return InstanceXform[instance];
}

float3 TransformPosition(GpuInstanceXform world, float3 position)
{
    float4 p = float4(position, 1.0);
    return float3(dot(world.r0, p), dot(world.r1, p), dot(world.r2, p));
}

// A direction (tangent): the linear part only, no translation.
float3 TransformDirection(GpuInstanceXform world, float3 direction)
{
    return float3(dot(world.r0.xyz, direction), dot(world.r1.xyz, direction), dot(world.r2.xyz, direction));
}

// Normals go through the cofactor matrix of the 3x3 linear part: cof(A) = det(A) * inverse(transpose(A)),
// which is the inverse-transpose without a division and is well defined for singular scales too. cof(A) * n
// points the wrong way when det < 0 (a mirrored instance), so the sign of the determinant is folded back in.
// Rows of A are the xyz of the transform rows; row i of cof(A) is cross(row i+1, row i+2), cyclically.
float3 TransformNormal(GpuInstanceXform world, float3 normal)
{
    float3 a0 = world.r0.xyz;
    float3 a1 = world.r1.xyz;
    float3 a2 = world.r2.xyz;
    float3 c0 = cross(a1, a2);
    float3 c1 = cross(a2, a0);
    float3 c2 = cross(a0, a1);
    float det = dot(a0, c0);
    float3 n = float3(dot(c0, normal), dot(c1, normal), dot(c2, normal));
    return normalize(n * (det < 0.0 ? -1.0 : 1.0));
}

#endif
