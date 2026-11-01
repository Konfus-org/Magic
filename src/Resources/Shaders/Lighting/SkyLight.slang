// The sky's light from everywhere, image-based: diffuse light from nine spherical-harmonic coefficients, reflections
// from a prefiltered environment cube read at the surface's roughness (the split sum, Karis 2013), both weighed for
// multiple scattering (Fdez-Aguera 2019) and darkened by the surface's ambient occlusion.

module "Lighting/SkyLight";

import Core.Heap;
import Core.Math;
import Core.Surface;
import Lighting.Brdf;
import Lighting.Lights;

// The sky's light as the renderer captured it this frame, Core's SkyLight member for member, which a pass's constants
// hold whole: the DFG table, the light from everywhere as nine harmonics, the reflections for each roughness, and the
// sampler they are read through.
public struct SkyLight
{
    TextureRef dfg;
    ArrayRef<float4> sh;     // rgb each harmonic's coefficient
    CubeRef specular;        // level m is GGX-filtered at roughness m / (levels - 1)
    SamplerRef sampler;      // linear, clamped

    // The DFG table at a view angle and roughness (Lighting/Brdf's IntegrateDfg).
    public float3 GetDfg(float nov, float roughness)
    {
        return dfg.SampleLevel(sampler, float2(nov, roughness), 0.0).xyz;
    }

    // The light a white Lambertian surface facing normal reflects.
    public float3 GetIrradiance(float3 normal)
    {
        return GetShIrradiance(sh, normal);
    }

    // The sky's reflection along a direction, as a surface of a roughness sees it.
    public float3 SampleSpecular(float3 direction, float roughness)
    {
        float levels = (float)specular.size.z;
        return specular.SampleLevel(sampler, direction, roughness * (levels - 1.0)).rgb;
    }
}

// The light a white Lambertian surface facing n reflects from an environment given as nine spherical-harmonic
// coefficients (L2, linear colour): the environment's radiance projected on the real SH basis, convolved here with
// the clamped cosine (Ramamoorthi & Hanrahan 2001), and divided by pi for the Lambertian BRDF. Multiplied by a
// diffuse colour, it is the diffuse luminance. The basis takes the engine's frame as it is (+Y up).
public float3 GetShIrradiance(ArrayRef<float4> coefficients, float3 n)
{
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = coefficients[i].rgb;
    }

    // Bands 0, 1 and 2 of the cosine lobe, over pi: 1, 2/3 and 1/4.
    float3 result = sh[0] * 0.282095;
    result += (sh[1] * n.y + sh[2] * n.z + sh[3] * n.x) * (0.488603 * (2.0 / 3.0));
    result += (sh[4] * (n.x * n.y) + sh[5] * (n.y * n.z) + sh[7] * (n.x * n.z)) * (1.092548 * 0.25);
    result += sh[6] * (0.315392 * 0.25 * (3.0 * n.z * n.z - 1.0));
    result += sh[8] * (0.546274 * 0.25 * (n.x * n.x - n.y * n.y));
    return max(result, 0.0);
}

// Where a rough surface's reflection mostly comes from: toward the normal from the mirror direction as roughness
// grows (Lagarde & de Rousiers 2014). Read the prefiltered cube along it.
public float3 GetDominantDirection(float3 n, float3 reflected, float roughness)
{
    float smoothness = 1.0 - roughness;
    return normalize(lerp(n, reflected, smoothness * (sqrt(smoothness) + roughness)));
}

// Ambient occlusion's visibility let bounce, so a bright surface fills in its own creases (Jimenez et al. 2016,
// eq. 10) and a white wall never goes grey where it meets another.
public float3 GetMultiBounceAo(float visibility, float3 albedo)
{
    float3 a = 2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c = 2.7552 * albedo + 0.6903;
    return max(visibility, ((visibility * a + b) * visibility + c) * visibility);
}

// How much of a highlight ambient occlusion hides (Lagarde & de Rousiers 2014): more on a rough surface, less at a
// grazing view.
public float GetSpecularOcclusion(float nov, float visibility, float roughness)
{
    return saturate(pow(nov + visibility, exp2(-16.0 * roughness - 1.0)) - 1.0 + visibility);
}

// The luminance a surface sends toward the viewer from its environment. irradiance is GetIrradiance at the normal;
// prefiltered the prefiltered radiance along GetDominantDirection at the surface's roughness, and coatPrefiltered the
// same at the coat's (read for clear coat only); each already less what occlusion hides of it. Cloth's sheen is lit by
// the irradiance, its lobe being wide; subsurface's transmission takes no ambient of its own.
public float3 ShadeAmbient(Shading shading, float3 irradiance, float3 prefiltered, float3 coatPrefiltered)
{
    // Single scattering reflects the prefiltered radiance; what bounces between microfacets leaves spread out, as
    // irradiance does; the diffuse layer gets what is left.
    float3 singleScatter = shading.f0 * shading.dfg.x + shading.f90 * shading.dfg.y;
    float albedo = shading.dfg.x + shading.dfg.y;
    float3 averageFresnel = shading.f0 + (1.0 - shading.f0) / 21.0;
    float3 multipleScatter = singleScatter * averageFresnel * (1.0 - albedo) / (1.0 - (1.0 - albedo) * averageFresnel);
    float3 diffuse = shading.diffuseColor * (1.0 - singleScatter - multipleScatter);
    float3 ambient = singleScatter * prefiltered + (multipleScatter + diffuse) * irradiance;

    switch (shading.model)
    {
    case ShadingModel.ClearCoat:
    {
        float coatFresnel = GetSchlickFresnel(CoatF0, 1.0, shading.nov) * shading.modelData.x;
        return ambient * (1.0 - coatFresnel) + coatPrefiltered * coatFresnel;
    }
    case ShadingModel.Cloth:
    {
        float3 sheenColor = shading.modelData.x * sqrt(shading.baseColor);
        float sheenBeneath = saturate(1.0 - Max3(sheenColor) * shading.sheenAlbedo);
        return ambient * sheenBeneath + sheenColor * shading.sheenAlbedo * irradiance;
    }
    default:
        return ambient;
    }
}
