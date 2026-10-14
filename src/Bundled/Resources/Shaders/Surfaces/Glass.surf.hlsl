// Glass: a transparent surface that bends what is behind it (Include/Surface.hlsli's SceneBehind, the scene as it was
// before the transparent surfaces). The view ray refracts into the surface by the index of refraction and runs on
// for the thickness; where it lands on screen, against where the straight ray would, is how far the scene behind
// shifts, so a curved face magnifies, shrinks and bends it as glass does. Red, green and blue refract a little
// differently (dispersion), which fringes the edges. The glass absorbs as it goes (Beer-Lambert): the tint is the
// colour of light that has crossed it straight through, so a grazing path, which is longer, is deeper coloured.
// Roughness frosts it, blurring both what it shows behind and what it reflects. Its reflections and highlights are
// the renderer's, at the glass's own reflectance (Fresnel, from the index of refraction); what it emits is the light
// it lets through, which the reflection takes its share from. Use it with "type": "transparent"; as an opaque
// material (no scene behind to bend) it is the tint.

#include "Include/Brdf.hlsli"
#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color tint : GiColor = Color(0.92, 0.97, 1.0, 1.0);
    float ior = 1.45;          // index of refraction: 1.45 glass, 1.33 water
    float thickness = 0.4;     // metres the bent ray runs before it meets the scene behind
    float dispersion = 0.015;  // how far apart red and blue refract, as a share of the index
    float roughness = 0.04;
    float normalScale = 1.0;
    TextureRef normalMap;
};

#if SURFACE_FORWARD
// What lies behind along the view ray bent by a face of normal n with this index of refraction, in the scene behind.
float3 Refracted(SurfaceInputs input, float3 toward, float3 n, float ior, float thickness, float roughness)
{
    float3 bent = refract(toward, n, 1.0 / ior);
    if (dot(bent, bent) < 1e-6)
    {
        bent = reflect(toward, n); // totally reflected inside: what the mirror shows
    }

    float2 straight = ScenePixelOf(input.worldPosition + toward * thickness);
    float2 landed = ScenePixelOf(input.worldPosition + bent * thickness);
    float2 here = ScenePixelOf(input.worldPosition);
    return SceneBehind(here + (landed - straight), roughness);
}
#endif

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);
    float3 n = input.normal;
    [branch] if (HasTexture(material.normalMap))
    {
        n = SampleNormal(input, material.normalMap, material.normalScale);
    }

    // The reflectance an index of refraction gives, as the specular that gives it: F0 = ((ior - 1) / (ior + 1))^2.
    float ior = max(material.ior, 1.0);
    float f0 = Pow2((ior - 1.0) / (ior + 1.0));
    surface.baseColor = material.tint.rgb;
    surface.normal = n;
    surface.roughness = material.roughness;
    surface.metallic = 0.0;
    surface.specular = sqrt(f0 / 0.16);

#if SURFACE_FORWARD
    float3 toward = -input.viewDirection;
    float3 behind = float3(
        Refracted(input, toward, n, ior * (1.0 - material.dispersion), material.thickness, material.roughness).r,
        Refracted(input, toward, n, ior, material.thickness, material.roughness).g,
        Refracted(input, toward, n, ior * (1.0 + material.dispersion), material.thickness, material.roughness).b);

    // A path of thickness / cos through the glass, against the tint's straight one.
    float cosInside = abs(dot(refract(toward, n, 1.0 / ior), n));
    float3 transmittance = pow(max(material.tint.rgb, 1e-4), 1.0 / max(cosInside, 0.2));
    float reflected = F_Schlick(f0, 1.0, saturate(dot(input.viewDirection, n)));

    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.emissive = behind * transmittance * (1.0 - reflected);
    surface.alpha = 1.0;
#endif
    return surface;
}
