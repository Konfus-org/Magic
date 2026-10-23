// The view graded, a thread a pixel: how saturated it is and how much contrast it has about middle grey, each made more
// (up to 1, twice) or less (down to -1: grey, or flat), a tint over it and a vignette darkening its corners. At zeros
// and a white tint it changes nothing. The Grade post effect's.

#include "Include/Color.hlsli"
#include "Include/Effect.hlsli"
#include "Include/Heap.hlsli"

struct GradeConstants
{
    EffectImages effect;     // its image the finished view so far
    float4 tint;             // multiplied over it, linear
    float saturation;        // 0 as it is
    float contrast;          // about middle grey, 0 as it is
    float vignette;          // how dark the corners go, 0 none
};

CONSTANTS(GradeConstants, c);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint2 size = SizeOf(LEVEL(c.effect.output, 0u));
    if (any(id.xy >= size))
    {
        return;
    }

    float3 color = TEXTURE(c.effect.image).Load(int3(id.xy, 0)).rgb;
    float grey = Luminance(color);
    color = lerp(float3(grey, grey, grey), color, 1.0 + c.saturation);
    color = (color - 0.18) * (1.0 + c.contrast) + 0.18;
    color *= c.tint.rgb;
    float2 fromMiddle = (float2(id.xy) + 0.5) / float2(size) - 0.5;
    color *= 1.0 - c.vignette * smoothstep(0.2, 0.8, dot(fromMiddle, fromMiddle) * 2.0);
    LEVEL(c.effect.output, 0u)[id.xy] = float4(saturate(color), 1.0);
}
