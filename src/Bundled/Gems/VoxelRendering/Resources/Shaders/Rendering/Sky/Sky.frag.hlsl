// The world's sky over what nothing was drawn on in a view (Skies.hlsli, composed before this): its light in nits along
// the pixel's direction, its sun's and moon's discs with it, pre-exposed, and what it gives off pre-exposed as it is.
// Each pixel's dither moves each frame, for a sky that integrates by steps.

#include "Include/Fullscreen.hlsli"
#include "Include/Noise.hlsli"
#include "Rendering/Include/SkyAlong.hlsli"

struct SkyConstants
{
    uint view;
    uint depth;              // the view's depth, 0 where nothing was drawn
    uint exposure;           // the view's exposure (Effects/Exposure.comp.hlsl)
};

CONSTANTS(SkyConstants, c);

float4 main(PassVaryings input) : SV_Target0
{
    LoadView(c.view);
    if (TEXTURE(c.depth).Load(int3(input.position.xy, 0)).r > 0.0)
    {
        discard;
    }

    float3 direction = normalize(ViewToWorld(ViewPosition(input.position.xy, 1.0)));
    SkyLight light = SkyAlong(direction, false, InterleavedGradientNoise(input.position.xy, View.frame.index));
    float exposure = BUFFER(c.exposure).Load<float4>(0u).x;
    return float4((light.nits + light.discs) * exposure + light.emissive, 1.0);
}
