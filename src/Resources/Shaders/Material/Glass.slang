// Glass: a pane that lets the light behind it through, tinted by its colour, and reflects what is in front of it. It
// draws over the lit picture, after the opaque surfaces. "clarity" is how much of the light behind gets through, 0 to
// 1; a colour map, as stained glass has, tints the pane further where it names one, its alpha letting less through; a
// normal map bends the surface the pane reflects with; a rougher pane spreads the sun's highlight.
module "Material/Glass";

import Core.Heap;
import Core.Math;
import Surface.Surface;
import Material.IMaterial;

public struct Glass : IMaterial
{
    public static const uint Translucent = 1;

    public float4 color;
    public float clarity;
    public float roughness;
    // The colour map and the normal map, and the samplers they are read with; 0 for none.
    public TextureRef colorMap;
    public SamplerRef colorMapSampler;
    public TextureRef normalMap;
    public SamplerRef normalMapSampler;

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        float4 base = color;
        if (colorMap.id != 0u)
            base *= colorMap.SampleGrad(colorMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy);

        float3 normal = inputs.normal;
        if (normalMap.id != 0u)
        {
            float3 bent = normalMap.SampleGrad(normalMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy).xyz * 2.0 - 1.0;
            float3 bitangent = cross(inputs.normal, inputs.tangent) * inputs.tangentSign;
            float3 shaded = inputs.tangent * bent.x + bitangent * bent.y + inputs.normal * bent.z;
            normal = NormalizeSafe(shaded, inputs.normal);
        }

        SurfaceOutputs surface;
        surface.baseColor = base.rgb;
        surface.alpha = 1.0 - saturate(clarity) * base.a;
        surface.normal = normal;
        surface.roughness = roughness;
        surface.metalness = 0.0;
        surface.emissive = float3(0.0);
        surface.occlusion = 1.0;
        return surface;
    }
}
