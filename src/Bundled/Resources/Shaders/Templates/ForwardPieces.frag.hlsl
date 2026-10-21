// The transparent template for pieces: composed after a surface and its LoadMaterialParams, it evaluates the surface and
// lights it as it is drawn, with the lighting the G-buffer gets (Lighting/Shade.hlsli), blended over what is behind it
// with premultiplied alpha. What a refracting surface emits is the scene behind it (SceneBehind), so it covers it
// (alpha 1) and shows it bent. Its roughness is widened by how far the normal turns across the pixel, as the G-buffer's
// is (Include/Brdf.hlsli's FilteredRoughness).

#include "Include/PieceFragment.hlsli"
#include "Include/Pieces.hlsli"
#include "Lighting/Shade.hlsli"

float4 main(PieceVaryings input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    float2 uvDdx = ddx(input.uv);
    float2 uvDdy = ddy(input.uv);
    float3 positionDdx = ddx(input.position);
    float3 positionDdy = ddy(input.position);
    float3 normal = NormalizeOrZero(input.normal);
    float3 normalDdx = ddx(normal) * TextureMipScale;
    float3 normalDdy = ddy(normal) * TextureMipScale;
    float cover;
    float surfaceDepth;
    SurfaceInputs inputs = PieceInputsOf(input, uvDdx, uvDdy, positionDdx, positionDdy, isFrontFace, cover, surfaceDepth);
    Surface surface = EvaluateSurface(inputs, LoadMaterialParams(input.material));
    clip(min(cover - 0.5, DissolveKeep(input.dissolve, input.clip.xy)));

    ShadePoint shaded;
    shaded.position = input.position;
    shaded.normal = NormalizeOrZero(surface.normal);
    shaded.view = inputs.viewDirection;
    shaded.baseColor = surface.baseColor;
    shaded.metallic = surface.metallic;
    shaded.specular = surface.specular;
    shaded.roughness = FilteredRoughness(surface.roughness, normalDdx, normalDdy);
    shaded.occlusion = surface.occlusion;
    shaded.model = surface.shadingModel;
    shaded.modelData = surface.modelData;
    shaded.pixel = input.clip.xy;
    shaded.viewDepth = input.clip.w;
    float3 lit = Shade(shaded) * LOAD(Exposure, float4, 0u).x;
    float3 color = lit * surface.alpha + surface.emissive;
    return float4(color, saturate(surface.alpha));
}
