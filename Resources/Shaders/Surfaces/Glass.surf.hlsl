// Glass: a transparent surface that bends what is behind it (Include/Surface.hlsli's SceneBehind, the scene as it was
// before the transparent surfaces). The view ray refracts into the surface by the index of refraction and runs on
// for the thickness; where it lands on screen, against where the straight ray would, is how far the scene behind
// shifts, so a curved face magnifies, shrinks and bends it as glass does. Red, green and blue refract a little
// differently (dispersion), which fringes the edges. The tint colours what passes through, the Fresnel term lets
// more of the sky's reflection through at grazing angles, and the template adds the highlights of the sun and the
// lights on top (black base colour: glass has no diffuse). Use it with "type": "transparent"; it covers what is
// behind it entirely, since it shows that itself. As an opaque material (no scene behind to bend) it is the tint.

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
float3 Refracted(SurfaceInputs input, float3 view, float3 n, float ior, float thickness)
{
    float3 bent = refract(view, n, 1.0 / ior);
    if (dot(bent, bent) < 1e-6)
        bent = reflect(view, n); // totally reflected inside: what the mirror shows
    float2 straight = ScenePixelOf(input.worldPosition + view * thickness);
    float2 landed = ScenePixelOf(input.worldPosition + bent * thickness);
    float2 here = input.screenUv * ViewSize;
    return SceneBehind(here + (landed - straight));
}
#endif

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);
    float3 n = input.normal;
    [branch] if (HasTexture(material.normalMap))
    {
        float3 tangentNormal = SampleTexture(input, material.normalMap).xyz * 2.0 - 1.0;
        tangentNormal.xy *= material.normalScale;
        n = PerturbNormal(input, tangentNormal);
    }

#if SURFACE_FORWARD
    float3 view = NormalizeOrZero(input.worldPosition - CameraPos);
    float ior = max(material.ior, 1.0);
    float3 behind = float3(
        Refracted(input, view, n, ior * (1.0 - material.dispersion), material.thickness).r,
        Refracted(input, view, n, ior, material.thickness).g,
        Refracted(input, view, n, ior * (1.0 + material.dispersion), material.thickness).b);

    // Schlick's Fresnel: the share reflected rather than let through.
    float facing = saturate(dot(-view, n));
    float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
    float fresnel = f0 + (1.0 - f0) * pow(1.0 - facing, 5.0);

    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.normal = n;
    surface.roughness = material.roughness;
    surface.metallic = 0.0;
    surface.emissive = behind * material.tint.rgb * (1.0 - fresnel) + Ambient * fresnel;
    surface.alpha = 1.0;
#else
    surface.baseColor = material.tint.rgb;
    surface.normal = n;
    surface.roughness = material.roughness;
#endif
    return surface;
}
