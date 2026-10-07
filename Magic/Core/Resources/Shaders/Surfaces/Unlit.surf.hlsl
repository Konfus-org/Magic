// A surface that ignores lighting: its colour is emitted as is. Useful for debug geometry and UI-like
// things in the world. See Include/Surface.hlsli for the contract.

#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color color : GiEmissive = Color(1.0, 1.0, 1.0, 1.0);
    TextureRef colorMap : GiColorMap;
    float alphaCutoff = 0.5;
};

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);

    float4 color = material.color;
    [branch] if (HasTexture(material.colorMap))
        color *= SampleTexture(input, material.colorMap);

    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.alpha = color.a;
    surface.emissive = color.rgb;
    surface.alphaCutoff = material.alphaCutoff;
    return surface;
}
