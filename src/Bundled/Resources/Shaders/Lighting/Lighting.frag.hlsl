// The lighting: once per pixel of the view, what the G-buffer says is there lit (Lighting/Shade.hlsli), pre-exposed,
// and added to the scene colour, which already holds what the surfaces emit. A pixel nothing was drawn on is left to
// the sky.

#include "Include/Fullscreen.hlsli"
#include "Include/GBuffer.hlsli"
#include "Lighting/Shade.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    float depth = Depth.SampleLevel(DepthSampler, input.uv, 0.0);
    if (depth <= 0.0)
    {
        discard;
    }

    GBufferSurface stored = DecodeGBuffer(
        Albedo.SampleLevel(AlbedoSampler, input.uv, 0.0),
        Normal.SampleLevel(NormalSampler, input.uv, 0.0),
        Material.SampleLevel(MaterialSampler, input.uv, 0.0));
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

    // Never, as depth is at most 1, but it keeps every read bound whatever of it the world's effects leave unused.
    float kept = depth > 2.0 ? KeepReadsAlive(input.uv) : 0.0;
    return float4(Shade(shaded) * LOAD(Exposure, float4, 0u).x + kept, 0.0);
}
