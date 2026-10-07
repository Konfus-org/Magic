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

// A pixel's place in the 4 x 4 ordered-dither (Bayer) matrix, as the middle of its sixteenth of 0..1: the
// bits of x ^ y and y interleaved, most significant last.
float DitherValue(float2 pixel)
{
    uint2 cell = (uint2)pixel & 3u;
    uint mixed = cell.x ^ cell.y;
    uint rank = ((mixed & 1u) << 3u) | ((cell.y & 1u) << 2u) | (mixed & 2u) | ((cell.y & 2u) >> 1u);
    return ((float)rank + 0.5) / 16.0;
}

#endif
