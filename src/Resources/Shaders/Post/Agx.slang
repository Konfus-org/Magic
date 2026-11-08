// The AgX tonemap (Sobotka, AgX, 2022): the exposed picture through AgX's base curve, which keeps the hue of what is
// bright as it rolls it off, and back to linear light for the display's encoding. ToAgx is the curve alone, for an
// effect that wraps it.
module "Post/Agx";

import Post.IPixelEffect;

// The matrices are written as columns, as the reference lists them, so a row vector goes through them on the left.
static const float3x3 ToAgxSpace = float3x3(
    0.842479062253094, 0.0423282422610123, 0.0423756549057051,
    0.0784335999999992, 0.878468636469772, 0.0784336,
    0.0792237451477643, 0.0791661274605434, 0.879142973793104
);
static const float3x3 FromAgxSpace = float3x3(
    1.19687900512017, -0.0528968517574562, -0.0529716355144438,
    -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
    -0.0990297440797205, -0.0989611768448433, 1.15107367264116
);

// The stops below and above a middle grey of 0.18 the curve spans.
static const float LeastEv = -12.47393;
static const float MostEv = 4.026069;

public struct Agx : IPixelEffect
{
    public float3 Apply(float3 color, PixelEffectInputs inputs) { return ToAgx(color); }
}

public float3 ToAgx(float3 color)
{
    float3 value = mul(max(color, 1e-10), ToAgxSpace);
    value = (clamp(log2(value), LeastEv, MostEv) - LeastEv) / (MostEv - LeastEv);

    // The base curve, a polynomial fit of the reference's sigmoid.
    float3 x2 = value * value;
    float3 x4 = x2 * x2;
    value = 15.5 * x4 * x2 - 40.14 * x4 * value + 31.96 * x4 - 6.868 * x2 * value + 0.4298 * x2 + 0.1191 * value - 0.00232;
    value = mul(saturate(value), FromAgxSpace);
    return pow(saturate(value), 2.2);
}
