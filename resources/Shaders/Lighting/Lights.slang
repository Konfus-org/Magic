// How a surface answers light: its BRDF terms for one view (Shading), the lights that reach it, and what it sends
// toward the viewer from each. A light is a struct implementing ILight; a renderer hands the world's directional
// lights in WorldLights and shades a point by them at once (ShadeLights), and its point and spot lights (LocalLight)
// one by one as its bins say (ShadeLight), each shadowed as an IShadowing says, then adds the sky's light from
// everywhere (Lighting/SkyLight) once.
//
// Units. A light's colour is linear Rec. 709 with a luminance of 1, and its strength is physical: a directional light
// (the sun) is illuminance, lux on a surface facing it, about 100 000 at a clear noon and 400 at sunrise; a point or
// spot light is luminous intensity, candela, 64 for an 800 lumen bulb. ShadeLight answers with luminance, in nits; the
// renderer multiplies it by exposure, as the scene colour is pre-exposed.
//
// A new kind of light is a struct implementing ILight, which a renderer shades with ShadeLight. A new kind of shadow is
// a struct implementing IShadowing, which the lighting pass hands ShadeLights and ShadeLight.

module "Lighting/Lights";

import Core.Math;
import Core.Surface;
import Lighting.Brdf;

// A surface's BRDF terms for one view, made once per pixel by CreateShading.
public struct Shading
{
    float3 normal;
    float3 view;               // toward the viewer
    float nov;                 // n.v, kept above 0
    float3 baseColor;
    float3 diffuseColor;
    float3 f0;
    float f90;
    float alpha;
    float2 dfg;                // the DFG table's x and y at (n.v, roughness)
    float3 energyCompensation; // multiple scattering, for lights
    ShadingModel model;
    float2 modelData;
    float sheenAlbedo;         // cloth: the DFG table's z at (n.v, sheen roughness)
}

// The light arriving from one light, as ShadeLight takes it, and what a shadowing needs to trace toward it.
public struct LightSample
{
    float3 l;              // toward the light's centre: what diffuse sees
    float3 specularL;      // toward the point of the light's area the reflection meets: what the highlight sees
    float specularScale;   // what the highlight's wider lobe gives back to keep its energy
    float3 illuminance;    // lux arriving at the surface, colour included, before shadowing
    float distance;        // metres to the light's surface along l; LightFar for a light infinitely far
    float tanHalfAngle;    // how wide the light looks from the surface: its radius over its distance, or its disc's
    bool isShadowless;     // its light reaches through everything
}

// The distance of a light infinitely far, which a trace toward it goes on to the end of what it can see.
public static const float LightFar = 1e30;

public interface ILight
{
    // The light arriving at a camera-relative position from this light, for a surface's terms.
    LightSample GetSample(Shading shading, float3 position);
}

public interface IShadowing
{
    // The share of a light's sample that reaches a camera-relative position on a surface, by colour: 1 where nothing is
    // in the way.
    float3 GetVisibility(Shading shading, float3 position, LightSample light);
}

// Nothing in the way of any light.
public struct Unshadowed : IShadowing
{
    public float3 GetVisibility(Shading shading, float3 position, LightSample light)
    {
        return float3(1.0, 1.0, 1.0);
    }
}

// The sun, or any light infinitely far. The highlight looks toward the point of the disc nearest the reflection
// (Lagarde & de Rousiers 2014), so a smooth surface shows the disc rather than a pinpoint, and its lobe widens by the
// disc as a sphere light's does by the sphere, scaled back to keep its energy, so a mirror's glint is no brighter than
// the sun.
public struct DirectionalLight : ILight
{
    float3 direction;        // the way its light travels, unit length
    float angularRadius;     // its disc's half-angle in radians: the sun's is 0.00465
    float3 illuminance;      // lux, times its colour; zero with none

    public LightSample GetSample(Shading shading, float3 position)
    {
        LightSample light;
        light.l = -direction;
        light.illuminance = illuminance;
        float widened = saturate(shading.alpha + tan(angularRadius) / 3.0);
        light.specularScale = Pow2(shading.alpha / max(widened, 1e-4));

        float3 reflected = reflect(-shading.view, shading.normal);
        float cosRadius = cos(angularRadius);
        float alongReflection = dot(light.l, reflected);
        float3 across = NormalizeOrZero(reflected - alongReflection * light.l);
        light.specularL = alongReflection < cosRadius
            ? normalize(cosRadius * light.l + across * sin(angularRadius))
            : reflected;
        light.distance = LightFar;
        light.tanHalfAngle = tan(angularRadius);
        light.isShadowless = false;
        return light;
    }
}

// A local light's flags.
public static const uint LocalLightShadowless = 1u;

// A point or spot light: a sphere of light shining every way, or in a cone along forward for a spot. Its light falls
// off with the square of the distance, windowed to nothing at its range (Karis 2013, "Real Shading in Unreal Engine
// 4"); a spot's fades from its inner angle to its outer as the square of a ramp in the cosine, which spotScale and
// spotOffset hold (Lagarde & de Rousiers 2014), a point light's scale 0 and offset 1. Its highlight looks toward the
// point of the sphere nearest the reflection (Karis 2013), its lobe widened by the sphere and scaled back to keep its
// energy, as the sun's is by its disc. The renderer's GpuLocalLight, member for member, but for position, which the
// renderer hands camera-relative.
public struct LocalLight : ILight
{
    float3 position;     // its centre
    float range;         // metres it reaches
    float3 intensity;    // candela, times its colour
    float radius;        // metres from its centre to its surface
    float3 forward;      // a spot's axis, the way its light travels
    float spotScale;
    float spotOffset;
    uint flags;          // LocalLightShadowless

    // Lux arriving at a camera-relative point from it, facing it, colour included, before shadowing.
    public float3 GetIlluminance(float3 at)
    {
        float3 toLight = position - at;
        float distanceSquared = max(dot(toLight, toLight), 1e-8);
        float window = Pow2(saturate(1.0 - Pow2(Pow2(sqrt(distanceSquared) / max(range, 1e-4)))));
        float spot = Pow2(saturate(dot(-toLight * rsqrt(distanceSquared), forward) * spotScale + spotOffset));
        return intensity * (window * spot / max(distanceSquared, Pow2(radius)));
    }

    public LightSample GetSample(Shading shading, float3 at)
    {
        float3 toLight = position - at;
        float distance = sqrt(max(dot(toLight, toLight), 1e-8));

        LightSample light;
        light.l = toLight / distance;
        light.illuminance = GetIlluminance(at);

        float3 reflected = reflect(-shading.view, shading.normal);
        float3 centreToRay = dot(toLight, reflected) * reflected - toLight;
        float3 nearest = toLight + centreToRay * saturate(radius / max(length(centreToRay), 1e-6));
        light.specularL = NormalizeOrZero(nearest);
        float widened = saturate(shading.alpha + radius / (3.0 * distance));
        light.specularScale = Pow2(shading.alpha / max(widened, 1e-4));

        light.distance = max(distance - radius, 0.0);
        light.tanHalfAngle = radius / distance;
        light.isShadowless = (flags & LocalLightShadowless) != 0u;
        return light;
    }
}

// The directional lights the world holds besides the key light: the renderer's DirectionalLights.Capacity.
public static const uint DirectionalLightCapacity = 4u;

// The lights a renderer lights the world by, the renderer's WorldLights member for member: the key light, the one that
// casts the shadows (the sun while it is up, else the moon while it is, else the brightest other), and the other
// directional lights, the first directionalCount.
public struct WorldLights
{
    DirectionalLight key;
    uint directionalCount;
    DirectionalLight directional[DirectionalLightCapacity];

    // Lux on a surface facing normal, from every light, colour included.
    public float3 GetIlluminance(float3 normal)
    {
        float3 lux = key.illuminance * saturate(dot(-key.direction, normal));
        [loop] for (uint i = 0u; i < min(directionalCount, DirectionalLightCapacity); i++)
        {
            lux += directional[i].illuminance * saturate(dot(-directional[i].direction, normal));
        }

        return lux;
    }
}

// A surface's terms for one view. dfg is the DFG table at (n.v, roughness); sheenAlbedo its z at (n.v, sheen
// roughness), read for cloth only.
public Shading CreateShading(
    float3 baseColor,
    float metallic,
    float specular,
    float roughness,
    float3 normal,
    float3 view,
    ShadingModel model,
    float2 modelData,
    float2 dfg,
    float sheenAlbedo)
{
    Shading shading;
    shading.normal = normal;
    shading.view = view;
    shading.nov = max(dot(normal, view), 1e-4);
    shading.baseColor = baseColor;
    shading.diffuseColor = GetDiffuseColor(baseColor, metallic);
    shading.f0 = GetSpecularF0(baseColor, metallic, specular);
    shading.f90 = GetF90(shading.f0);
    shading.alpha = ToGgxAlpha(roughness);
    shading.dfg = dfg;
    shading.energyCompensation = 1.0 + shading.f0 * (1.0 / max(dfg.x + dfg.y, 1e-4) - 1.0);
    shading.model = model;
    shading.modelData = modelData;
    shading.sheenAlbedo = model == ShadingModel.Cloth ? sheenAlbedo : 0.0;
    return shading;
}

// The luminance a surface sends toward the viewer from one light, before that light's shadowing.
public float3 ShadeLight(Shading shading, LightSample light)
{
    float nolCentre = dot(shading.normal, light.l);
    float nol = saturate(nolCentre);
    float specularNol = saturate(dot(shading.normal, light.specularL));
    float3 h = normalize(shading.view + light.specularL);
    float noh = saturate(dot(shading.normal, h));
    float loh = saturate(dot(light.specularL, h));

    float3 fresnel = GetSchlickFresnel(shading.f0, shading.f90, loh);
    float lobe = GetGgxDistribution(noh, shading.alpha)
        * GetSmithVisibility(shading.nov, specularNol, shading.alpha)
        * light.specularScale
        * specularNol;
    float3 specular = lobe * fresnel * shading.energyCompensation;

    // What the specular reflects first is not there to scatter beneath it.
    float3 diffuse = shading.diffuseColor * (1.0 - fresnel) * (GetLambertDiffuse() * nol);

    switch (shading.model)
    {
    case ShadingModel.ClearCoat:
    {
        // The coat reflects first; the layers beneath get what it lets through.
        float coatFresnel = GetSchlickFresnel(CoatF0, 1.0, loh) * shading.modelData.x;
        float coat = GetGgxDistribution(noh, ToGgxAlpha(shading.modelData.y))
            * GetKelemenVisibility(loh)
            * coatFresnel
            * specularNol;
        return ((diffuse + specular) * (1.0 - coatFresnel) + coat) * light.illuminance;
    }
    case ShadingModel.Cloth:
    {
        // The sheen sits on top and takes its own albedo from what is beneath.
        float3 sheenColor = shading.modelData.x * sqrt(shading.baseColor);
        float3 sheen = sheenColor * (GetCharlieDistribution(noh, ToGgxAlpha(shading.modelData.y))
            * GetNeubeltVisibility(shading.nov, specularNol)
            * specularNol);
        float beneath = saturate(1.0 - Max3(sheenColor) * shading.sheenAlbedo);
        return ((diffuse + specular) * beneath + sheen) * light.illuminance;
    }
    case ShadingModel.Subsurface:
    {
        // Light wraps past the terminator, and some passes through from behind, tinted by the surface.
        float wrapped = GetWrapDiffuse(nolCentre, shading.modelData.y);
        float through = saturate(-nolCentre) * shading.modelData.x * InvPi;
        return (shading.diffuseColor * (wrapped + through) + specular) * light.illuminance;
    }
    default:
        return (diffuse + specular) * light.illuminance;
    }
}

// The luminance a surface sends toward the viewer from one light, shadowed as shadowing says; a light that gives the
// surface nothing is not traced.
public float3 ShadeLight<L : ILight, S : IShadowing>(Shading shading, float3 position, L light, S shadowing)
{
    LightSample sample = light.GetSample(shading, position);
    float3 luminance = ShadeLight(shading, sample);
    [branch] if (sample.isShadowless || all(luminance <= 0.0))
    {
        return luminance;
    }

    return luminance * shadowing.GetVisibility(shading, position, sample);
}

// The luminance a surface sends toward the viewer from the world's directional lights, each shadowed as shadowing says.
public float3 ShadeLights<S : IShadowing>(WorldLights lights, Shading shading, float3 position, S shadowing)
{
    float3 luminance = float3(0.0, 0.0, 0.0);
    [branch] if (any(lights.key.illuminance > 0.0))
    {
        luminance += ShadeLight(shading, position, lights.key, shadowing);
    }

    [loop] for (uint i = 0u; i < min(lights.directionalCount, DirectionalLightCapacity); i++)
    {
        luminance += ShadeLight(shading, position, lights.directional[i], shadowing);
    }

    return luminance;
}
