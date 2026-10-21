// Bloom's first level (Post/Bloom.frag.hlsl): the view as it is shown, at half its size, each texel thirteen bilinear
// taps of the pixels under and around it, read as five boxes of four that overlap, the middle one counting as much as
// the four at its corners together (Jimenez 2014, "Next Generation Post Processing in Call of Duty: Advanced Warfare"),
// so the halving does not alias as what is bright moves across the pixels. Each box is weighed down by its brightness,
// as that talk does on the first level only, so a lone bright pixel gives the glow no more than its neighbours do and
// does not flicker as it moves. The renderer makes the coarser levels from this one once it is drawn. It reads the view
// upscaled where the frame upscales it (Taa), else the scene colour as it was drawn.

#include "Include/Color.hlsli"
#include "Include/Fullscreen.hlsli"

float3 Shown(float2 uv)
{
#if defined(HAS_Taa)
    return Taa.SampleLevel(TaaSampler, uv, 0.0).rgb;
#else
    return Hdr.SampleLevel(HdrSampler, uv, 0.0).rgb;
#endif
}

// A box of four taps, its mean times its share of the texel, the less the brighter the box is; total gathers the
// weights given.
float3 Box(float3 a, float3 b, float3 c, float3 d, float share, inout float total)
{
    float3 mean = (a + b + c + d) * 0.25;
    float weight = share / (1.0 + Luminance(mean));
    total += weight;
    return mean * weight;
}

float4 main(PassVaryings input) : SV_Target0
{
    float2 size;
#if defined(HAS_Taa)
    Taa.GetDimensions(size.x, size.y);
#else
    Hdr.GetDimensions(size.x, size.y);
#endif

    // The taps, in the view's pixels from the texel's middle, which lies between four of them: an outer ring two
    // apart, and an inner square one apart, each tap the mean of the four pixels about it.
    float2 pixel = 1.0 / size;
    float3 a = Shown(input.uv + pixel * float2(-2.0, -2.0));
    float3 b = Shown(input.uv + pixel * float2(0.0, -2.0));
    float3 c = Shown(input.uv + pixel * float2(2.0, -2.0));
    float3 d = Shown(input.uv + pixel * float2(-1.0, -1.0));
    float3 e = Shown(input.uv + pixel * float2(1.0, -1.0));
    float3 f = Shown(input.uv + pixel * float2(-2.0, 0.0));
    float3 g = Shown(input.uv);
    float3 h = Shown(input.uv + pixel * float2(2.0, 0.0));
    float3 i = Shown(input.uv + pixel * float2(-1.0, 1.0));
    float3 j = Shown(input.uv + pixel * float2(1.0, 1.0));
    float3 k = Shown(input.uv + pixel * float2(-2.0, 2.0));
    float3 l = Shown(input.uv + pixel * float2(0.0, 2.0));
    float3 m = Shown(input.uv + pixel * float2(2.0, 2.0));

    float total = 0.0;
    float3 sum = Box(d, e, i, j, 0.5, total);
    sum += Box(a, b, f, g, 0.125, total);
    sum += Box(b, c, g, h, 0.125, total);
    sum += Box(f, g, k, l, 0.125, total);
    sum += Box(g, h, l, m, 0.125, total);
    return float4(sum / total, 1.0);
}
