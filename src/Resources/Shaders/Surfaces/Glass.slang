// Glass: a clear dielectric, its colour the tint of light that has crossed it, its reflectance the Fresnel of its index
// of refraction. Roughness frosts what it reflects. Its reflections and highlights are the renderer's.

module "Surfaces/Glass";

import Core.Surface;

public struct Glass : ISurface
{
    [GiColor] Color tint = { 0.92, 0.97, 1.0, 1.0 };
    float ior = 1.45;        // index of refraction: 1.45 glass, 1.33 water
    float roughness = 0.04;
    float normalScale = 1.0;
    TextureRef normalMap = TextureNone;

    public Surface Evaluate(SurfaceInputs input)
    {
        Surface surface = CreateDefaultSurface(input);
        float3 n = input.normal;
        [branch] if (normalMap.isValid)
        {
            n = input.SampleNormal(normalMap, normalScale);
        }

        // The reflectance an index of refraction gives, as the specular that gives it: F0 = ((ior - 1) / (ior + 1))^2.
        float clampedIor = max(ior, 1.0);
        float f0 = Pow2((clampedIor - 1.0) / (clampedIor + 1.0));
        surface.baseColor = tint.rgb;
        surface.normal = n;
        surface.roughness = roughness;
        surface.metallic = 0.0;
        surface.specular = sqrt(f0 / 0.16);
        return surface;
    }
}
