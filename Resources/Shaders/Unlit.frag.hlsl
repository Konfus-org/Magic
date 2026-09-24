// Temporary M2 fragment shader (retired by the G-buffer at M5): base colour from the material and its pooled
// texture, a fixed-direction Lambert term so the faces of a mesh read as different shades, and the normal
// map when the material has one. Both pool samplers are used so the reflected sampler count is the full
// eight, exactly what every later material pass binds. Output is linear into the sRGB colour target.
#include "Include/Bindings.hlsli"
#include "Include/Structs.hlsli"
#include "Include/Material.hlsli"

struct PsIn
{
    float3 worldPos : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float2 uv : TEXCOORD3;
    nointerpolation uint material : TEXCOORD4;
};

// Towards the light: from above, the right and the camera's side of the default scenes (+X, +Y, +Z).
static const float3 LightDirection = normalize(float3(0.5, 1.0, 0.5));
static const float Ambient = 0.18;

float4 main(PsIn input) : SV_Target0
{
    GpuMaterial material = Materials[input.material];
    float2 uv = MaterialUv(material, input.uv);

    float4 baseColor = material.baseColor;
    [branch] if (material.textures.x != TEXTURE_NONE)
        baseColor *= SampleSrgb(material.textures.x, uv);

    float3 n = normalize(input.normal);
    [branch] if (material.textures.y != TEXTURE_NONE)
    {
        // Tangent frame from the vertex data; bitangent = cross(n, t) * sign, as the importer defines it.
        float3 t = normalize(input.tangent.xyz);
        float3 b = cross(n, t) * input.tangent.w;
        float3 tn = SampleLinear(material.textures.y, uv).xyz * 2.0 - 1.0;
        tn.xy *= material.roughMetalNormalFlags.z;
        n = normalize(t * tn.x + b * tn.y + n * tn.z);
    }

    float diffuse = saturate(dot(n, LightDirection));
    float3 color = baseColor.rgb * (Ambient + (1.0 - Ambient) * diffuse) + material.emissiveAlphaCutoff.xyz;
    return float4(color, 1.0);
}
