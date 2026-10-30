// The lit view to what a display shows: AgX (Sobotka 2022, as Blender 4 configures it), which desaturates bright light
// towards white smoothly rather than skewing its hue. The colour, its exposure's bias added, is taken into Rec. 2020,
// AgX's working space, then into its inset primaries, encoded in log2 over its 16.5 stops, put through its contrast
// curve (a polynomial fit of it, Wrensch 2023), and turned back out; the curve's output is display-encoded, so it is
// decoded to the linear colour the output encodes again, back in Rec. 709. Core's Tonemap's, member for member.

module "Post/Tonemap";

import Core.Color;
import Core.Post;

static const float3x3 Rec2020ToRec709 = float3x3(
    1.6604910, -0.5876411, -0.0728499,
    -0.1245505, 1.1328999, -0.0083494,
    -0.0181508, -0.1005789, 1.1187297);

static const float3x3 Inset = float3x3(
    0.856627153315983, 0.0951212405381588, 0.0482516061458583,
    0.137318972929847, 0.761241990602591, 0.101439036467562,
    0.11189821299995, 0.0767994186031903, 0.811302368396859);

static const float3x3 Outset = float3x3(
    1.1271005818144368, -0.1106066430966032, -0.0164939387178346,
    -0.1413297634984383, 1.1578237022162720, -0.0164939387178343,
    -0.1413297634984383, -0.1106066430966029, 1.2519364065950405);

static const float MinEv = -12.47393;
static const float MaxEv = 4.026069;

float3 GetContrastCurve(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

float3 ApplyAgX(float3 color)
{
    float3 inset = mul(Inset, ToRec2020(max(color, 0.0)));
    float3 encoded = saturate((log2(max(inset, 1e-10)) - MinEv) / (MaxEv - MinEv));
    float3 curved = mul(Outset, GetContrastCurve(encoded));
    return saturate(mul(Rec2020ToRec709, pow(max(curved, 0.0), 2.2)));
}

public struct Tonemap : IPixelEffect
{
    float bias;              // EV added before the curve to the lit view, which is pre-exposed

    public float3 Apply(float3 color, PixelInputs input)
    {
        return ApplyAgX(color * exp2(bias));
    }
}
