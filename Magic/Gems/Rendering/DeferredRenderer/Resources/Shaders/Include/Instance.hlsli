// The instance tables as the vertex stage reads them, and how a transform is applied: three float4 rows of
// transpose(world) per instance (see Structs.hlsli), applied with dot() because storage buffers carry no
// matrix types.

#ifndef MAGIC_INSTANCE_HLSLI
#define MAGIC_INSTANCE_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Structs.hlsli"

StructuredBuffer<GpuInstanceXform> InstanceXforms : READ(0);
StructuredBuffer<GpuInstance> Instances : READ(1);

float3 TransformPosition(GpuInstanceXform world, float3 position)
{
    float4 point4 = float4(position, 1.0);
    return float3(dot(world.r0, point4), dot(world.r1, point4), dot(world.r2, point4));
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
    float3 row0 = world.r0.xyz;
    float3 row1 = world.r1.xyz;
    float3 row2 = world.r2.xyz;

    float3 cofactor0 = cross(row1, row2);
    float3 cofactor1 = cross(row2, row0);
    float3 cofactor2 = cross(row0, row1);

    float3 transformed = float3(dot(cofactor0, normal), dot(cofactor1, normal), dot(cofactor2, normal));
    float determinant = dot(row0, cofactor0);
    return normalize(determinant < 0.0 ? -transformed : transformed);
}

#endif
