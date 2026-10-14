// How ambient occlusion darkens the light from everywhere: its visibility let bounce, so a bright surface fills in its
// own creases (Jimenez et al. 2016, eq. 10) and a white wall never goes grey where it meets another; and how much of a
// highlight it hides, from the visibility cone around the bent normal where there is one (Jimenez et al. 2016,
// "Practical Real-Time Strategies for Accurate Indirect Occlusion"), else from visibility alone (Lagarde & de
// Rousiers 2014): more on a rough surface, less at a grazing view.

#ifndef MAGIC_AMBIENT_HLSLI
#define MAGIC_AMBIENT_HLSLI

#include "Include/Math.hlsli"

float3 MultiBounceAo(float visibility, float3 albedo)
{
    float3 a = 2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c = 2.7552 * albedo + 0.6903;
    return max(visibility, ((visibility * a + b) * visibility + c) * visibility);
}

float SpecularOcclusion(float normalDotView, float visibility, float roughness)
{
    return saturate(pow(normalDotView + visibility, exp2(-16.0 * roughness - 1.0)) - 1.0 + visibility);
}

// The solid angle two spherical caps share, approximated (Oat & Sander 2007): caps whose half-angles have cosines
// cosA and cosB, with axes whose angle has cosine cosAxes.
float SphericalCapIntersection(float cosA, float cosB, float cosAxes)
{
    float angleA = FastAcos(cosA);
    float angleB = FastAcos(cosB);
    float angleAxes = FastAcos(cosAxes);
    float smaller = TwoPi - TwoPi * max(cosA, cosB);
    if (angleAxes <= abs(angleA - angleB))
    {
        return smaller;
    }

    if (angleAxes >= angleA + angleB)
    {
        return 0.0;
    }

    float difference = abs(angleA - angleB);
    float overlap = 1.0 - saturate((angleAxes - difference) / (angleA + angleB - difference));
    return smaller * smoothstep(0.0, 1.0, overlap);
}

// The share of a highlight's lobe, a cone around the reflection as wide as roughness makes it, that falls inside the
// cone the ambient occlusion found open around the bent normal: a crease hides the reflections it would hide from a
// mirror.
float SpecularOcclusionCone(float3 bentNormal, float visibility, float3 reflected, float roughness)
{
    float cosVisible = sqrt(saturate(1.0 - visibility));
    float cosLobe = exp2(-3.32193 * roughness * roughness);
    float common = SphericalCapIntersection(cosVisible, cosLobe, dot(bentNormal, reflected));
    return saturate(common / max(TwoPi - TwoPi * cosLobe, 1e-4));
}

#endif
