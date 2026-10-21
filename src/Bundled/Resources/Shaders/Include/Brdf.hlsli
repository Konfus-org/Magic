// The reflectance functions of the metallic-roughness model and its extra lobes: the one copy every renderer
// shades with. n is the normal, v toward the viewer, l toward the light, h halfway between; all unit length.
// Roughness is perceptual, what a material states, 0..1; alpha = roughness^2 is what the distributions take.
//
// Sources: Walter et al. 2007 (GGX); Heitz 2014 (height-correlated Smith); Karis 2013 (the split sum); Lagarde &
// de Rousiers 2014, "Moving Frostbite to PBR" (units, F90); Fdez-Aguera 2019 (multiple scattering); Kelemen &
// Szirmay-Kalos 2001 (clear coat visibility); Estevez & Kulla 2017 and Neubelt & Pettineo 2013 (cloth);
// Duff et al. 2017 (the tangent frame); Tokuyoshi & Kaplanyan 2019 (specular anti-aliasing).

#ifndef MAGIC_BRDF_HLSLI
#define MAGIC_BRDF_HLSLI

#include "Include/Math.hlsli"
#include "Include/Noise.hlsli"

// Below this a highlight is smaller than a pixel and only aliases, and 32-bit precision runs out in D_Ggx.
static const float MinRoughness = 0.045;

// What every dielectric clear coat reflects at normal incidence: 4 %, an index of refraction of 1.5.
static const float CoatF0 = 0.04;

// How much the normal's turn across a pixel widens a highlight (Tokuyoshi & Kaplanyan 2019, "Improved Geometric
// Specular Antialiasing"): the variance of the pixel's filter, 1 / 2 pi, and the most it adds to alpha squared.
static const float SpecularFilterVariance = 0.15915494;
static const float SpecularFilterLimit = 0.18;

float RoughnessToAlpha(float roughness)
{
    float clamped = clamp(roughness, MinRoughness, 1.0);
    return clamped * clamped;
}

// A perceptual roughness widened by how much the normal turns across the pixel, given its change a pixel across and a
// pixel down: the pixel's own spread of normals is added to the surface's as variances add, in alpha squared, so a
// highlight narrower than the pixel, on a small or far curved surface, is spread over it rather than flickering as the
// view moves. Where the normal does not turn, the roughness is as it was.
float FilteredRoughness(float roughness, float3 normalAcross, float3 normalDown)
{
    float added = min(2.0 * SpecularFilterVariance * (dot(normalAcross, normalAcross) + dot(normalDown, normalDown)), SpecularFilterLimit);
    float alpha = Pow2(saturate(roughness));
    return sqrt(sqrt(saturate(alpha * alpha + added)));
}

// The reflectance at normal incidence of a dielectric whose specular is 0..1: 0.5 is 4 %, what most non-metals
// reflect; 1 is 16 %, gemstones.
float DielectricF0(float specular)
{
    return 0.16 * specular * specular;
}

// What a surface reflects at normal incidence: a metal its base colour, a dielectric its own grey F0.
float3 SpecularF0(float3 baseColor, float metallic, float specular)
{
    return lerp(DielectricF0(specular).xxx, baseColor, metallic);
}

// What a surface reflects at grazing angles: all of it, except that an F0 under 2 %, which no real material has,
// means occluded, so its grazing reflection fades too (Schueler 2009). A specular of 0 turns reflection off.
float F90(float3 f0)
{
    return saturate(50.0 * dot(f0, float3(0.3333, 0.3333, 0.3333)));
}

float3 DiffuseColor(float3 baseColor, float metallic)
{
    return baseColor * (1.0 - metallic);
}

// GGX (Trowbridge-Reitz) normal distribution.
float D_Ggx(float noh, float alpha)
{
    float a2 = alpha * alpha;
    float d = (noh * a2 - noh) * noh + 1.0;
    return a2 / (Pi * d * d);
}

// Height-correlated Smith masking-shadowing, with the 1 / (4 n.l n.v) of the microfacet model folded in.
float V_SmithGgxCorrelated(float nov, float nol, float alpha)
{
    float a2 = alpha * alpha;
    float lambdaV = nol * sqrt((nov - nov * a2) * nov + a2);
    float lambdaL = nov * sqrt((nol - nol * a2) * nol + a2);
    return 0.5 / max(lambdaV + lambdaL, 1e-6);
}

// Schlick's Fresnel, from f0 at normal incidence toward f90 at grazing.
float3 F_Schlick(float3 f0, float f90, float voh)
{
    return f0 + (f90 - f0) * Pow5(1.0 - voh);
}

float F_Schlick(float f0, float f90, float voh)
{
    return f0 + (f90 - f0) * Pow5(1.0 - voh);
}

float Fd_Lambert()
{
    return InvPi;
}

// Diffuse that wraps past the terminator by wrap, 0..1, and still conserves energy: thin, translucent surfaces.
float Fd_Wrap(float nol, float wrap)
{
    return saturate((nol + wrap) / Pow2(1.0 + wrap)) * InvPi;
}

// Kelemen's visibility, cheap and good enough for a smooth clear coat.
float V_Kelemen(float loh)
{
    return 0.25 / max(loh * loh, 1e-4);
}

// The "Charlie" sheen distribution of cloth: an inverted Gaussian that peaks at grazing angles.
float D_Charlie(float noh, float alpha)
{
    float invAlpha = 1.0 / alpha;
    float sin2h = max(1.0 - noh * noh, 0.0078125);
    return (2.0 + invAlpha) * pow(sin2h, invAlpha * 0.5) / TwoPi;
}

// Neubelt's visibility, which Charlie's sheen pairs with.
float V_Neubelt(float nov, float nol)
{
    return saturate(1.0 / (4.0 * (nol + nov - nol * nov)));
}

// A direction in the frame of the unit normal n (+z along n) turned into the frame n is in, with no branch and no
// singularity (Duff et al. 2017, "Building an Orthonormal Basis, Revisited").
float3 TangentToWorld(float3 local, float3 n)
{
    float side = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (side + n.z);
    float b = n.x * n.y * a;
    float3 tangent = float3(1.0 + side * n.x * n.x * a, side * b, -side * n.x);
    float3 bitangent = float3(b, side + n.y * n.y * a, -n.y);
    return local.x * tangent + local.y * bitangent + local.z * n;
}

// A half vector in the normal's frame (+z), drawn in proportion to GGX's distribution from a uniform 2D sample.
float3 ImportanceSampleGgx(float2 random, float alpha)
{
    float a2 = alpha * alpha;
    float phi = TwoPi * random.x;
    float cosTheta = sqrt((1.0 - random.y) / (1.0 + (a2 - 1.0) * random.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    return float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
}

// What a DFG lookup table holds at a view angle and roughness, integrated by sampleCount samples:
//   x, y  the GGX lobe's directional albedo with F0 and F90 factored out, F0 * x + F90 * y (the split sum's
//         second factor, Karis 2013); x + y is the albedo at F = 1, which multiple scattering makes up to 1.
//   z     the Charlie sheen lobe's albedo, which cloth takes from the layers beneath its sheen.
// A compute pass makes the table once; a renderer reads it at (n.v, roughness).
float3 IntegrateDfg(float nov, float roughness, uint sampleCount)
{
    float alpha = RoughnessToAlpha(roughness);
    float3 v = float3(sqrt(1.0 - nov * nov), 0.0, nov);
    float3 sum = float3(0.0, 0.0, 0.0);
    for (uint i = 0u; i < sampleCount; i++)
    {
        float2 random = Hammersley(i, sampleCount);

        // GGX, importance sampled: f cos / pdf = V F n.l 4 v.h / n.h.
        float3 h = ImportanceSampleGgx(random, alpha);
        float voh = saturate(dot(v, h));
        float3 l = 2.0 * voh * h - v;
        float nol = saturate(l.z);
        if (nol > 0.0)
        {
            float weight = V_SmithGgxCorrelated(nov, nol, alpha) * nol * 4.0 * voh / max(h.z, 1e-6);
            float fresnel = Pow5(1.0 - voh);
            sum.xy += float2(1.0 - fresnel, fresnel) * weight;
        }

        // Charlie, sampled uniformly over the hemisphere: pdf = 1 / 2 pi.
        float cosTheta = 1.0 - random.y;
        float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
        float3 toLight = float3(sinTheta * cos(TwoPi * random.x), sinTheta * sin(TwoPi * random.x), cosTheta);
        float3 halfway = normalize(v + toLight);
        sum.z += D_Charlie(halfway.z, alpha) * V_Neubelt(nov, toLight.z) * toLight.z * TwoPi;
    }

    return sum / float(sampleCount);
}

#endif
