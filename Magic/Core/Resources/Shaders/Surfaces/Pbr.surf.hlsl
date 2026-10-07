// The default surface: metallic-roughness PBR inputs from factors and optional maps. See
// Include/Surface.hlsli for the contract. Defaults and the Gi* roles are read by the renderer and stripped
// before compilation; a .mat file states only the values that differ from the defaults.

#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color color : GiColor = Color(0.8, 0.8, 0.8, 1.0);
    float roughness = 0.6;
    float metallic = 0.0;
    float normalScale = 1.0;
    float alphaCutoff = 0.5;
    float3 emissive : GiEmissive = float3(0.0, 0.0, 0.0);
    TextureRef colorMap : GiColorMap;
    TextureRef normalMap;
    TextureRef ormMap; // occlusion, roughness, metallic in r, g, b
    TextureRef emissiveMap;
};

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);

    float4 color = material.color;
    [branch] if (HasTexture(material.colorMap))
        color *= SampleTexture(input, material.colorMap);
    surface.baseColor = color.rgb;
    surface.alpha = color.a;

    float3 orm = float3(1.0, material.roughness, material.metallic);
    [branch] if (HasTexture(material.ormMap))
        orm *= SampleTexture(input, material.ormMap).rgb;
    surface.occlusion = orm.r;
    surface.roughness = orm.g;
    surface.metallic = orm.b;

    [branch] if (HasTexture(material.normalMap))
    {
        float3 tangentNormal = SampleTexture(input, material.normalMap).xyz * 2.0 - 1.0;
        tangentNormal.xy *= material.normalScale;
        surface.normal = PerturbNormal(input, tangentNormal);
    }

    surface.emissive = material.emissive;
    [branch] if (HasTexture(material.emissiveMap))
        surface.emissive *= SampleTexture(input, material.emissiveMap).rgb;

    surface.alphaCutoff = material.alphaCutoff;
    return surface;
}
