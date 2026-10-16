// The lighting: once per pixel of the view, what the G-buffer says is there lit (Lighting/Shade.hlsli), pre-exposed,
// and added to the scene colour, which already holds what the surfaces emit. A pixel nothing was drawn on is left to
// the sky. A pixel lit to a NaN or an infinity, which every pass after this would spread, is drawn as the failure
// instead, where it is; the histogram raises it in words (Post/ExposureHistogram.comp.hlsl).

#include "Include/Failure.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/GBuffer.hlsli"
#include "Lighting/Shade.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    int3 pixel = int3(input.position.xy, 0);
    float depth = Depth.Load(pixel);
    if (depth <= 0.0)
    {
        discard;
    }

    GBufferSurface stored = DecodeGBuffer(Albedo.Load(pixel), Normal.Load(pixel), Material.Load(pixel));
    float viewDepth = ViewDepth(depth);
    float3 position = ViewToWorld(ViewPosition(input.position.xy, viewDepth));

    ShadePoint shaded;
    shaded.position = position;
    shaded.normal = stored.normal;
    shaded.view = -normalize(position);
    shaded.baseColor = stored.baseColor;
    shaded.metallic = stored.metallic;
    shaded.specular = stored.specular;
    shaded.roughness = stored.roughness;
    shaded.occlusion = stored.occlusion;
    shaded.model = stored.model;
    shaded.modelData = stored.modelData;
    shaded.pixel = input.position.xy;
    shaded.viewDepth = viewDepth;
    float3 lit = Shade(shaded) * LOAD(Exposure, float4, 0u).x;
    [branch] if (any(isnan(lit)) || any(isinf(lit)))
    {
        return float4(FailurePixel(Time), 0.0);
    }

    return float4(lit, 0.0);
}
