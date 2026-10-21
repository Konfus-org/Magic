// The scene to what a display shows: AgX (Sobotka 2022, as Blender 4 configures it), which desaturates bright light
// towards white smoothly rather than skewing its hue. The colour is taken into Rec. 2020 (ITU-R BT.2087), AgX's
// working space, then into its inset primaries, encoded in log2 over its 16.5 stops, put through its contrast curve (a
// polynomial fit of it, Wrensch 2023), and turned back out; the curve's output is display-encoded, so it is decoded to
// the linear Ldr the output encodes again, back in Rec. 709. It reads the view as it is shown, sharpened where the
// frame sharpens it (Sharpened, Post/Sharpen.comp.hlsl), else upscaled where the frame upscales it (Taa), else the
// scene colour as it was drawn, and where the world's effects bloom it, lays the glow over it first
// (Post/Bloom.frag.hlsl).

#include "Include/Fullscreen.hlsli"

struct PassParams
{
    float exposureBias = 0.0;   // EV added before the curve
};

static const float3x3 Rec709To2020 = float3x3(
    0.6274040, 0.3292820, 0.0433136,
    0.0690970, 0.9195400, 0.0113612,
    0.0163916, 0.0880132, 0.8955950);

static const float3x3 Rec2020To709 = float3x3(
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

float3 Contrast(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

float3 AgX(float3 color)
{
    float3 inset = mul(Inset, mul(Rec709To2020, max(color, 0.0)));
    float3 encoded = saturate((log2(max(inset, 1e-10)) - MinEv) / (MaxEv - MinEv));
    float3 curved = mul(Outset, Contrast(encoded));
    return saturate(mul(Rec2020To709, pow(max(curved, 0.0), 2.2)));
}

float4 main(PassVaryings input) : SV_Target0
{
    PassParams params = LoadPassParams();
#if defined(HAS_Sharpened)
    float3 color = Sharpened.SampleLevel(SharpenedSampler, input.uv, 0.0).rgb;
#elif defined(HAS_Taa)
    float3 color = Taa.SampleLevel(TaaSampler, input.uv, 0.0).rgb;
#else
    float3 color = Hdr.SampleLevel(HdrSampler, input.uv, 0.0).rgb;
#endif
#if defined(HAS_Bloom)
    // The glow, premultiplied, over what the view keeps of its light.
    float4 bloom = Bloom.SampleLevel(BloomSampler, input.uv, 0.0);
    color = color * (1.0 - bloom.a) + bloom.rgb;
#endif

    return float4(AgX(color * exp2(params.exposureBias)), 1.0);
}
