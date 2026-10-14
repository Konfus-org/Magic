// Light from everywhere: the sky and reflection probes as image-based lighting. Diffuse light comes from nine
// spherical-harmonic coefficients, reflections from a prefiltered environment cube read at the surface's roughness
// (the split sum, Karis 2013), and both are weighed for multiple scattering (Fdez-Aguera 2019).

#ifndef MAGIC_IBL_HLSLI
#define MAGIC_IBL_HLSLI

#include "Lighting/Lights.hlsli"

// The light a white Lambertian surface facing n reflects from an environment given as nine spherical-harmonic
// coefficients (L2, linear colour): the environment's radiance projected on the real SH basis, convolved here with
// the clamped cosine (Ramamoorthi & Hanrahan 2001), and divided by pi for the Lambertian BRDF. Multiplied by a
// diffuse colour, it is the diffuse luminance. The basis takes the engine's frame as it is (+Y up).
float3 ShIrradiance(float3 n, float3 sh[9])
{
    // Bands 0, 1 and 2 of the cosine lobe, over pi: 1, 2/3 and 1/4.
    float3 result = sh[0] * 0.282095;
    result += (sh[1] * n.y + sh[2] * n.z + sh[3] * n.x) * (0.488603 * (2.0 / 3.0));
    result += (sh[4] * (n.x * n.y) + sh[5] * (n.y * n.z) + sh[7] * (n.x * n.z)) * (1.092548 * 0.25);
    result += sh[6] * (0.315392 * 0.25 * (3.0 * n.z * n.z - 1.0));
    result += sh[8] * (0.546274 * 0.25 * (n.x * n.x - n.y * n.y));
    return max(result, 0.0);
}

// The mip of a prefiltered environment cube that holds a roughness: mip m is GGX-filtered at roughness
// m / (mipCount - 1), which is how the prefiltering pass makes them.
float PrefilteredLevel(float roughness, float mipCount)
{
    return roughness * (mipCount - 1.0);
}

// Where a rough surface's reflection mostly comes from: toward the normal from the mirror direction as roughness
// grows (Lagarde & de Rousiers 2014). Read the prefiltered cube along it.
float3 SpecularDominantDirection(float3 n, float3 reflected, float roughness)
{
    float smoothness = 1.0 - roughness;
    return normalize(lerp(n, reflected, smoothness * (sqrt(smoothness) + roughness)));
}

// A reflection direction corrected for a local probe's box (Lagarde 2012, "Local Image-based Lighting With Parallax-
// corrected Cubemaps"): where the ray from position along direction leaves the box, as seen from the probe.
float3 BoxProjectedDirection(float3 position, float3 direction, float3 boxMin, float3 boxMax, float3 probePosition)
{
    float3 toMax = (boxMax - position) / direction;
    float3 toMin = (boxMin - position) / direction;
    float3 farthest = max(toMax, toMin);
    float distance = min(min(farthest.x, farthest.y), farthest.z);
    return position + direction * distance - probePosition;
}

// The luminance a surface sends toward the viewer from its environment. irradiance is ShIrradiance at the normal (or
// a probe's); prefiltered the prefiltered radiance along SpecularDominantDirection at the surface's roughness, and
// coatPrefiltered the same at the coat's (read for clear coat only); the occlusions come from ambient occlusion
// (Lighting/Ambient.hlsli). Cloth's sheen is lit by the irradiance, its lobe being wide; subsurface's transmission
// takes no ambient of its own.
float3 ShadeAmbient(Shading shading, float3 irradiance, float3 prefiltered, float3 coatPrefiltered, float diffuseOcclusion, float specularOcclusion)
{
    // Single scattering reflects the prefiltered radiance; what bounces between microfacets leaves spread out, as
    // irradiance does; the diffuse layer gets what is left.
    float3 singleScatter = shading.f0 * shading.dfg.x + shading.f90 * shading.dfg.y;
    float albedo = shading.dfg.x + shading.dfg.y;
    float3 averageFresnel = shading.f0 + (1.0 - shading.f0) / 21.0;
    float3 multipleScatter = singleScatter * averageFresnel * (1.0 - albedo) / (1.0 - (1.0 - albedo) * averageFresnel);
    float3 diffuse = shading.diffuseColor * (1.0 - singleScatter - multipleScatter);
    float3 ambient = singleScatter * prefiltered * specularOcclusion + (multipleScatter + diffuse) * irradiance * diffuseOcclusion;

    [branch] if (shading.model == ShadingClearCoat)
    {
        float coatFresnel = F_Schlick(CoatF0, 1.0, shading.nov) * shading.modelData.x;
        return ambient * (1.0 - coatFresnel) + coatPrefiltered * (coatFresnel * specularOcclusion);
    }

    [branch] if (shading.model == ShadingCloth)
    {
        float3 sheenColor = shading.modelData.x * sqrt(shading.baseColor);
        return ambient * saturate(1.0 - Max3(sheenColor) * shading.sheenAlbedo) + sheenColor * (shading.sheenAlbedo * diffuseOcclusion) * irradiance;
    }

    return ambient;
}

#endif
