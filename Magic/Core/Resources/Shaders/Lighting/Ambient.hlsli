// How the ambient light is occluded: the ambient occlusion's visibility, let bounce (a bright surface fills in
// its own creases, Jimenez et al. 2016 eq. 10) so a white wall never goes grey where it meets another; and
// how a highlight is occluded by it (Lagarde and de Rousiers 2014), more on a rough surface, less at a
// grazing view.

#ifndef MAGIC_AMBIENT_HLSLI
#define MAGIC_AMBIENT_HLSLI

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

#endif
