// Colour as the renderer keeps it: linear Rec. 709 radiance, which textures and the window hold sRGB-encoded.
module "Core/Color";

// The sRGB encoding of linear light, by the standard's own piecewise curve (IEC 61966-2-1).
public float ToSrgb(float linear)
{
    return linear <= 0.0031308 ? linear * 12.92 : 1.055 * pow(linear, 1.0 / 2.4) - 0.055;
}

public float3 ToSrgb(float3 linear) { return float3(ToSrgb(linear.r), ToSrgb(linear.g), ToSrgb(linear.b)); }

// How bright a linear Rec. 709 colour is, by the standard's own weights (ITU-R BT.709).
public float GetLuminance(float3 linear) { return dot(linear, float3(0.2126, 0.7152, 0.0722)); }
