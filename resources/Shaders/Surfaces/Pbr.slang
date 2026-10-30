// The default surface: metallic-roughness PBR inputs from factors and optional maps (Core/Surface's contract). A .mat
// file states only the values that differ from the defaults.

module "Surfaces/Pbr";

import Core.Surface;

public struct Pbr : ISurface
{
    [GiColor] Color color = { 0.8, 0.8, 0.8, 1.0 };
    float roughness = 0.6;
    [GiMetallic] float metallic = 0.0;
    float specular = 0.5;    // a dielectric's reflectance: 0.5 is 4 %, what most non-metals reflect
    float normalScale = 1.0;
    float alphaCutoff = 0.5;
    [GiEmissive] float3 emissive = float3(0.0, 0.0, 0.0);
    [GiColorMap] TextureRef colorMap = TextureNone;
    TextureRef normalMap = TextureNone;
    TextureRef ormMap = TextureNone;      // occlusion, roughness, metallic in r, g, b
    TextureRef emissiveMap = TextureNone;

    public Surface Evaluate(SurfaceInputs input)
    {
        Surface surface = CreateDefaultSurface(input);

        float4 color = this.color.rgba;
        [branch] if (colorMap.isValid)
        {
            color *= input.Sample(colorMap);
        }

        surface.baseColor = color.rgb;
        surface.alpha = color.a;

        float3 orm = float3(1.0, roughness, metallic);
        [branch] if (ormMap.isValid)
        {
            orm *= input.Sample(ormMap).rgb;
        }

        surface.occlusion = orm.r;
        surface.roughness = orm.g;
        surface.metallic = orm.b;
        surface.specular = specular;

        [branch] if (normalMap.isValid)
        {
            surface.normal = input.SampleNormal(normalMap, normalScale);
        }

        surface.emissive = emissive;
        [branch] if (emissiveMap.isValid)
        {
            surface.emissive *= input.Sample(emissiveMap).rgb;
        }

        surface.alphaCutoff = alphaCutoff;
        return surface;
    }
}
