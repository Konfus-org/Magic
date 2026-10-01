// The few maths helpers more than one shader needs.

#ifndef MAGIC_MATH_HLSLI
#define MAGIC_MATH_HLSLI

static const float Pi = 3.14159265;

// normalize() that returns zero for a zero vector instead of NaN: a mesh without tangents has zero tangents,
// and a NaN would spread to everything computed from it.
float3 NormalizeOrZero(float3 direction)
{
    return direction * rsqrt(max(dot(direction, direction), 1e-20));
}

#endif
