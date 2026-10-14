// The maths helpers more than one shader needs.

#ifndef MAGIC_MATH_HLSLI
#define MAGIC_MATH_HLSLI

static const float Pi = 3.14159265;
static const float TwoPi = 6.28318531;
static const float HalfPi = 1.57079633;
static const float InvPi = 0.31830989;

float Pow2(float x)
{
    return x * x;
}

// x^5 in three multiplies: Schlick's Fresnel and its kin.
float Pow5(float x)
{
    float x2 = x * x;
    return x2 * x2 * x;
}

float Max3(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

// normalize() that returns zero for a zero vector instead of NaN: a mesh without tangents has zero tangents,
// and a NaN would spread to everything computed from it.
float3 NormalizeOrZero(float3 direction)
{
    return direction * rsqrt(max(dot(direction, direction), 1e-20));
}

// acos() to within about 0.0007 radians, from a square root and a multiply-add (Eberly 2014), where acos's own
// cost shows: the angles of cones and caps per pixel.
float FastAcos(float x)
{
    float ax = abs(x);
    float angle = (-0.156583 * ax + HalfPi) * sqrt(1.0 - ax);
    return x >= 0.0 ? angle : Pi - angle;
}

#endif
