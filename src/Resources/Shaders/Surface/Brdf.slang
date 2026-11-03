// How a surface reflects a light: a Lambert diffuse under a GGX specular with Smith's height-correlated masking and
// Schlick's Fresnel (Walter et al., "Microfacet Models for Refraction through Rough Surfaces", 2007; Heitz,
// "Understanding the Masking-Shadowing Function", 2014), as every lighting pass evaluates it.
module "Surface/Brdf";

import Core.Math;

public struct Brdf
{
    public float3 diffuse;
    public float3 f0;
    // Perceptual roughness squared: what the distribution takes.
    public float alpha;
}

// A surface's terms from what the GBuffer keeps of it.
public Brdf MakeBrdf(float3 baseColor, float metalness, float roughness, float specular)
{
    Brdf brdf;
    brdf.diffuse = baseColor * (1.0 - metalness);
    brdf.f0 = lerp(float3(0.08 * specular), baseColor, metalness);
    brdf.alpha = max(roughness * roughness, 0.002);
    return brdf;
}

float DistributionGgx(float alpha, float nDotH)
{
    float a2 = alpha * alpha;
    float d = (nDotH * nDotH * (a2 - 1.0)) + 1.0;
    return a2 / max(Pi * d * d, Epsilon);
}

float VisibilitySmith(float alpha, float nDotV, float nDotL)
{
    float a2 = alpha * alpha;
    float v = nDotL * sqrt(nDotV * nDotV * (1.0 - a2) + a2);
    float l = nDotV * sqrt(nDotL * nDotL * (1.0 - a2) + a2);
    return 0.5 / max(v + l, Epsilon);
}

float3 FresnelSchlick(float3 f0, float vDotH) { return f0 + (1.0 - f0) * Pow5(1.0 - vDotH); }

// The light leaving towards the viewer for a unit of irradiance from the light's direction: the BRDF times the
// cosine, both lobes.
public float3 Evaluate(Brdf brdf, float3 normal, float3 view, float3 light)
{
    float nDotL = saturate(dot(normal, light));
    if (nDotL <= 0.0)
        return float3(0.0);

    float3 half = NormalizeSafe(view + light, normal);
    float nDotV = max(dot(normal, view), 1e-4);
    float nDotH = saturate(dot(normal, half));
    float vDotH = saturate(dot(view, half));
    float3 specular = DistributionGgx(brdf.alpha, nDotH) * VisibilitySmith(brdf.alpha, nDotV, nDotL) * FresnelSchlick(brdf.f0, vDotH);
    return (brdf.diffuse * InvPi + specular) * nDotL;
}

// What the surface reflects of light from everywhere at once, as the sky's: the diffuse under the average Fresnel.
public float3 EvaluateAmbient(Brdf brdf, float3 normal, float3 view)
{
    float nDotV = max(dot(normal, view), 1e-4);
    float3 fresnel = brdf.f0 + (max(float3(1.0 - brdf.alpha), brdf.f0) - brdf.f0) * Pow5(1.0 - nDotV);
    return brdf.diffuse * (1.0 - fresnel) + fresnel * (1.0 - brdf.alpha * 0.5);
}
