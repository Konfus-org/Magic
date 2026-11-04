// Colour as the renderer keeps it: linear Rec. 709 radiance, which textures and the window hold sRGB-encoded.
module "Core/Color";

// Linear light from an sRGB-encoded value, by the standard's own piecewise curve (IEC 61966-2-1).
public float ToLinear(float srgb)
{
    return srgb <= 0.04045 ? srgb / 12.92 : pow((srgb + 0.055) / 1.055, 2.4);
}

public float3 ToLinear(float3 srgb) { return float3(ToLinear(srgb.r), ToLinear(srgb.g), ToLinear(srgb.b)); }

// The sRGB encoding of linear light, by the standard's own piecewise curve (IEC 61966-2-1).
public float ToSrgb(float linear)
{
    return linear <= 0.0031308 ? linear * 12.92 : 1.055 * pow(linear, 1.0 / 2.4) - 0.055;
}

public float3 ToSrgb(float3 linear) { return float3(ToSrgb(linear.r), ToSrgb(linear.g), ToSrgb(linear.b)); }

// How bright a linear Rec. 709 colour is, by the standard's own weights (ITU-R BT.709).
public float GetLuminance(float3 linear) { return dot(linear, float3(0.2126, 0.7152, 0.0722)); }

// The relative exposure that brings linear light to a middle grey of 0.18 at exposure value ev100 (ISO 100): what
// the meter hands the frame to multiply its radiance by.
public float GetExposure(float ev100) { return 1.0 / (1.2 * exp2(ev100)); }
