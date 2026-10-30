// The maths more than one shader needs: constants, small powers, a normalize that never gives NaN, unit vectors packed
// on the octahedron, and the directions of a cube's faces.

module "Core/Math";

public static const float Pi = 3.14159265;
public static const float TwoPi = 6.28318531;
public static const float InvPi = 0.31830989;

public float Pow2(float x)
{
    return x * x;
}

// x^5 in three multiplies: Schlick's Fresnel and its kin.
public float Pow5(float x)
{
    float x2 = x * x;
    return x2 * x2 * x;
}

public float Max3(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

public float Min3(float3 value)
{
    return min(value.x, min(value.y, value.z));
}

// normalize() that returns zero for a zero vector instead of NaN: a mesh without tangents has zero tangents,
// and a NaN would spread to everything computed from it.
public float3 NormalizeOrZero(float3 direction)
{
    return direction * rsqrt(max(dot(direction, direction), 1e-20));
}

// A unit vector as two values in -1..1 on the octahedron (Cigolle et al. 2014, "A Survey of Efficient
// Representations for Independent Unit Vectors"): precision spread evenly over the sphere, with no pole to
// break at. In 10 + 10 bits it stays within about 0.2 degrees.
public float2 EncodeOctahedral(float3 direction)
{
    float3 projected = direction / (abs(direction.x) + abs(direction.y) + abs(direction.z));
    float2 signs = select(projected.xy >= 0.0, float2(1.0, 1.0), float2(-1.0, -1.0));
    float2 folded = (1.0 - abs(projected.yx)) * signs;
    return projected.z >= 0.0 ? projected.xy : folded;
}

public float3 DecodeOctahedral(float2 encoded)
{
    float3 direction = float3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    float fold = saturate(-direction.z);
    direction.xy += select(direction.xy >= 0.0, float2(-fold, -fold), float2(fold, fold));
    return normalize(direction);
}

// The unit direction a point of a cube's face looks along: faces as every API lays them out, +X, -X, +Y, -Y, +Z, -Z,
// a face's uv from its top left.
public float3 GetCubeDirection(uint face, float2 uv)
{
    float2 st = uv * 2.0 - 1.0;
    float3 direction;
    switch (face)
    {
    case 0u: direction = float3(1.0, -st.y, -st.x); break;
    case 1u: direction = float3(-1.0, -st.y, st.x); break;
    case 2u: direction = float3(st.x, 1.0, st.y); break;
    case 3u: direction = float3(st.x, -1.0, -st.y); break;
    case 5u: direction = float3(-st.x, -st.y, -1.0); break;
    default: direction = float3(st.x, -st.y, 1.0); break;
    }

    return normalize(direction);
}
