// Colour: the sRGB transfer function, luminance and YCoCg, all on linear Rec. 709 (the sRGB primaries).

#ifndef MAGIC_COLOR_HLSLI
#define MAGIC_COLOR_HLSLI

// The light a linear colour gives off as the eye weighs it (Rec. 709 luma coefficients).
float Luminance(float3 linearRgb)
{
    return dot(linearRgb, float3(0.2126, 0.7152, 0.0722));
}

// The exact sRGB transfer function, both ways: the window's image is UNORM, so the last pass of a frame encodes
// its colour itself.
float3 SrgbToLinear(float3 encoded)
{
    float3 value = max(encoded, 0.0);
    return select(value <= 0.04045, value / 12.92, pow((value + 0.055) / 1.055, 2.4));
}

float3 LinearToSrgb(float3 linearRgb)
{
    float3 value = max(linearRgb, 0.0);
    return select(value <= 0.0031308, value * 12.92, 1.055 * pow(value, 1.0 / 2.4) - 0.055);
}

// Luminance and two chroma axes, from adds and shifts: temporal filters clip history to the current frame's
// neighbourhood in this space, where the box hugs the colours far tighter than in RGB (Karis 2014).
float3 RgbToYCoCg(float3 rgb)
{
    float co = rgb.r - rgb.b;
    float t = rgb.b + co * 0.5;
    float cg = rgb.g - t;
    return float3(t + cg * 0.5, co, cg);
}

float3 YCoCgToRgb(float3 ycocg)
{
    float t = ycocg.x - ycocg.z * 0.5;
    float g = ycocg.z + t;
    float b = t - ycocg.y * 0.5;
    return float3(b + ycocg.y, g, b);
}

#endif
