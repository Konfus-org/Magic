// How a surface reflects a light: a Lambert diffuse, less what the specular's Fresnel reflects, under a GGX specular
// with Smith's height-correlated masking and Schlick's Fresnel (Walter et al., "Microfacet Models for Refraction
// through Rough Surfaces", 2007; Heitz, "Understanding the Masking-Shadowing Function", 2014), as every lighting pass
// evaluates it; and the specular's response to light from every direction, for what an environment reflects.
module "Surface/Brdf";

import Core.Math;
import Core.Sampling;

public struct Brdf
{
    public float3 diffuse;
    public float3 f0;
    // Perceptual roughness squared: what the distribution takes.
    public float alpha;
}

// A surface's terms from what the GBuffer keeps of it.
public Brdf CreateBrdf(float3 baseColor, float metalness, float roughness, float specular)
{
    Brdf brdf;
    brdf.diffuse = baseColor * (1.0 - metalness);
    brdf.f0 = lerp(float3(0.08 * specular), baseColor, metalness);
    brdf.alpha = max(roughness * roughness, 0.002);
    return brdf;
}

// The density of microfacet normals along the half vector.
public float DistributionGgx(float alpha, float nDotH)
{
    float a2 = alpha * alpha;
    float d = (nDotH * nDotH * (a2 - 1.0)) + 1.0;
    return a2 / max(Pi * d * d, Epsilon);
}

// The share of microfacets both the view and the light see, over the 4 nDotV nDotL the specular divides by.
public float VisibilitySmith(float alpha, float nDotV, float nDotL)
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
    float3 fresnel = FresnelSchlick(brdf.f0, vDotH);
    float3 specular = DistributionGgx(brdf.alpha, nDotH) * VisibilitySmith(brdf.alpha, nDotV, nDotL) * fresnel;
    return (brdf.diffuse * (1.0 - fresnel) * InvPi + specular) * nDotL;
}

// The share of light from every direction the specular reflects towards the viewer, by perceptual roughness: the
// pre-integrated GGX response fitted analytically (Karis, "Physically Based Shading on Mobile", 2014).
public float3 GetEnvironmentBrdf(float3 f0, float roughness, float nDotV)
{
    float4 r = roughness * float4(-1.0, -0.0275, -0.572, 0.022) + float4(1.0, 0.0425, 1.04, -0.04);
    float a004 = min(r.x * r.x, exp2(-9.28 * nDotV)) * r.x + r.y;
    float2 scaleBias = float2(-1.04, 1.04) * a004 + r.zw;
    return f0 * scaleBias.x + scaleBias.y;
}

// A microfacet normal the view sees, drawn from the GGX distribution of the visible ones (Heitz, "Sampling the GGX
// Distribution of Visible Normals", 2018), in the frame whose z is the surface's normal.
public float3 SampleGgxVisibleNormal(float3 viewLocal, float alpha, float2 xi)
{
    float3 stretched = normalize(float3(alpha * viewLocal.x, alpha * viewLocal.y, viewLocal.z));
    float lengthSquared = stretched.x * stretched.x + stretched.y * stretched.y;
    float3 tangent = lengthSquared > 0.0 ? float3(-stretched.y, stretched.x, 0.0) * rsqrt(lengthSquared) : float3(1.0, 0.0, 0.0);
    float3 bitangent = cross(stretched, tangent);
    float radius = sqrt(xi.x);
    float phi = TwoPi * xi.y;
    float p1 = radius * cos(phi);
    float p2 = radius * sin(phi);
    float s = 0.5 * (1.0 + stretched.z);
    p2 = (1.0 - s) * sqrt(max(0.0, 1.0 - p1 * p1)) + s * p2;
    float3 half = p1 * tangent + p2 * bitangent + sqrt(max(0.0, 1.0 - p1 * p1 - p2 * p2)) * stretched;
    return normalize(float3(alpha * half.x, alpha * half.y, max(half.z, 0.0)));
}

// A direction the specular reflects the view into, in xyz, from a microfacet normal drawn as SampleGgxVisibleNormal
// draws it with a tenth of its tail cut, which only adds noise, and that normal's density in w: the mirror direction
// at a density of 1 where the surface is all but smooth.
public float4 SampleGgxReflection(float3 normal, float3 view, float roughness, float2 xi)
{
    if (roughness <= 0.05)
        return float4(reflect(-view, normal), 1.0);

    float alpha = Pow2(max(roughness, 0.045));
    float3x3 basis = CreateBasis(normal);
    float3 viewLocal = ToLocal(basis, view);
    float3 half = SampleGgxVisibleNormal(viewLocal, alpha, float2(xi.x * 0.9, xi.y));
    float nDotV = max(viewLocal.z, 1e-4);
    float a2 = alpha * alpha;
    float masking = 2.0 * nDotV / (sqrt(nDotV * (nDotV - nDotV * a2) + a2) + nDotV);
    float pdf = masking * saturate(dot(viewLocal, half)) * DistributionGgx(alpha, half.z) / nDotV;
    return float4(reflect(-view, ToWorld(basis, half)), pdf);
}

// What the surface reflects of light from everywhere at once, as the sky's: the diffuse under the average Fresnel.
public float3 EvaluateAmbient(Brdf brdf, float3 normal, float3 view)
{
    float nDotV = max(dot(normal, view), 1e-4);
    float3 fresnel = brdf.f0 + (max(float3(1.0 - brdf.alpha), brdf.f0) - brdf.f0) * Pow5(1.0 - nDotV);
    return brdf.diffuse * (1.0 - fresnel) + fresnel * (1.0 - brdf.alpha * 0.5);
}
