// How a surface answers light: its BRDF terms for one view (Shading), the lights that reach it, and what it sends
// toward the viewer from each. A renderer prepares a Shading per pixel, adds ShadeLight for every light that reaches
// it, shadowed, and ShadeAmbient (Lighting/Ibl.hlsli) once.
//
// Units. A light's colour is linear Rec. 709, and its strength is physical:
//   directional (the sun)  illuminance, lux on a surface facing it: about 100 000 at a clear noon, 400 at sunrise.
//   point                  luminous intensity, candela = lumens / 4 pi: an 800 lm bulb is 64 cd.
//   spot                   candela = lumens / pi, so narrowing the cone does not brighten it (Frostbite's choice),
//                          masked by SpotAttenuation.
// ShadeLight answers with luminance, in nits; the renderer multiplies it by exposure, as the scene colour is
// pre-exposed.

#ifndef MAGIC_LIGHTS_HLSLI
#define MAGIC_LIGHTS_HLSLI

#include "Include/Brdf.hlsli"
#include "Include/ShadingModel.hlsli"

// A surface's BRDF terms for one view, made once per pixel by PrepareShading.
struct Shading
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
    uint model;
    float2 modelData;
    float sheenAlbedo;         // cloth: the DFG table's z at (n.v, sheen roughness)
};

// The light arriving from one light, as ShadeLight takes it.
struct LightSample
{
    float3 l;              // toward the light's centre: what diffuse sees
    float3 specularL;      // toward the point of the light's area the reflection meets: what the highlight sees
    float specularScale;   // what the highlight's wider lobe gives back to keep its energy
    float3 illuminance;    // lux arriving at the surface, colour included, before shadowing
};

// A surface's terms for one view. dfg is the DFG table at (n.v, roughness); sheenAlbedo its z at (n.v, sheen
// roughness), read for cloth only.
Shading PrepareShading(float3 baseColor, float metallic, float specular, float roughness, float3 normal, float3 view, uint model, float2 modelData, float2 dfg, float sheenAlbedo)
{
    Shading shading;
    shading.normal = normal;
    shading.view = view;
    shading.nov = max(dot(normal, view), 1e-4);
    shading.baseColor = baseColor;
    shading.diffuseColor = DiffuseColor(baseColor, metallic);
    shading.f0 = SpecularF0(baseColor, metallic, specular);
    shading.f90 = F90(shading.f0);
    shading.alpha = RoughnessToAlpha(roughness);
    shading.dfg = dfg;
    shading.energyCompensation = 1.0 + shading.f0 * (1.0 / max(dfg.x + dfg.y, 1e-4) - 1.0);
    shading.model = model;
    shading.modelData = modelData;
    shading.sheenAlbedo = model == ShadingCloth ? sheenAlbedo : 0.0;
    return shading;
}

// How much of a light's intensity reaches a point toLight away: the inverse square, faded smoothly to nothing at the
// light's range so its influence has an edge to cull by (Karis 2013). invRangeSq is 1 / range^2. Nearer than a
// centimetre counts as a centimetre.
float DistanceAttenuation(float3 toLight, float invRangeSq)
{
    float distanceSq = dot(toLight, toLight);
    float ratio = distanceSq * invRangeSq;
    float window = Pow2(saturate(1.0 - ratio * ratio));
    return window / max(distanceSq, 1e-4);
}

// How much of a spot light's cone covers the direction l (toward the light): 1 inside the inner cone, nothing outside
// the outer, a smooth step between. forward is the way the light shines; scaleOffset is (1 / (cos inner - cos
// outer), -cos outer * that), worked out once per light.
float SpotAttenuation(float3 l, float3 forward, float2 scaleOffset)
{
    float inside = saturate(dot(-l, forward) * scaleOffset.x + scaleOffset.y);
    return inside * inside;
}

// The sun, or any light infinitely far: forward is the way it shines (its entity's +Z), illuminance its lux times its
// colour, and angularRadius its disc's half-angle in radians (the sun's is 0.00465). The highlight looks toward the
// point of the disc nearest the reflection (Lagarde & de Rousiers 2014), so a smooth surface shows the disc rather
// than a pinpoint.
LightSample DirectionalLight(Shading shading, float3 forward, float3 illuminance, float angularRadius)
{
    LightSample light;
    light.l = -forward;
    light.illuminance = illuminance;
    light.specularScale = 1.0;

    float3 reflected = reflect(-shading.view, shading.normal);
    float cosRadius = cos(angularRadius);
    float alongReflection = dot(light.l, reflected);
    float3 across = NormalizeOrZero(reflected - alongReflection * light.l);
    light.specularL = alongReflection < cosRadius ? normalize(cosRadius * light.l + across * sin(angularRadius)) : reflected;
    return light;
}

// A point or spot light: a sphere radius metres across at toLight from the surface, intensity its candela times its
// colour (and a spot's SpotAttenuation). The highlight looks toward the point of the sphere nearest the reflection,
// and its lobe widens to match, scaled back to keep its energy (Karis 2013, "Real Shading in Unreal Engine 4").
LightSample SphereLight(Shading shading, float3 toLight, float3 intensity, float radius, float invRangeSq)
{
    LightSample light;
    float distanceSq = max(dot(toLight, toLight), 1e-8);
    light.l = toLight * rsqrt(distanceSq);
    light.illuminance = intensity * DistanceAttenuation(toLight, invRangeSq);

    float3 reflected = reflect(-shading.view, shading.normal);
    float3 toRay = dot(toLight, reflected) * reflected - toLight;
    float3 nearest = toLight + toRay * saturate(radius * rsqrt(max(dot(toRay, toRay), 1e-8)));
    light.specularL = normalize(nearest);
    float widened = saturate(shading.alpha + radius / (3.0 * sqrt(distanceSq)));
    light.specularScale = Pow2(shading.alpha / max(widened, 1e-4));
    return light;
}

// The luminance a surface sends toward the viewer from one light, before that light's shadowing.
// The bits of a mask word that are lights first to last.
uint RangeMask(uint word, uint first, uint last)
{
    uint start = word * 32u;
    bool isOutside = first > last || last < start || first > start + 31u;
    uint low = max(first, start) - start;
    uint high = min(last, start + 31u) - start;
    return isOutside ? 0u : (0xFFFFFFFFu >> (31u - high)) & (0xFFFFFFFFu << low);
}

float3 ShadeLight(Shading shading, LightSample light)
{
    float nolCentre = dot(shading.normal, light.l);
    float nol = saturate(nolCentre);
    float specularNol = saturate(dot(shading.normal, light.specularL));
    float3 h = normalize(shading.view + light.specularL);
    float noh = saturate(dot(shading.normal, h));
    float loh = saturate(dot(light.specularL, h));

    float3 fresnel = F_Schlick(shading.f0, shading.f90, loh);
    float lobe = D_Ggx(noh, shading.alpha) * V_SmithGgxCorrelated(shading.nov, specularNol, shading.alpha) * light.specularScale * specularNol;
    float3 specular = lobe * fresnel * shading.energyCompensation;
    float3 diffuse = shading.diffuseColor * (Fd_Lambert() * nol);

    [branch] if (shading.model == ShadingClearCoat)
    {
        // The coat reflects first; the layers beneath get what it lets through.
        float coatFresnel = F_Schlick(CoatF0, 1.0, loh) * shading.modelData.x;
        float coat = D_Ggx(noh, RoughnessToAlpha(shading.modelData.y)) * V_Kelemen(loh) * coatFresnel * specularNol;
        return ((diffuse + specular) * (1.0 - coatFresnel) + coat) * light.illuminance;
    }

    [branch] if (shading.model == ShadingCloth)
    {
        // The sheen sits on top and takes its own albedo from what is beneath.
        float3 sheenColor = shading.modelData.x * sqrt(shading.baseColor);
        float3 sheen = sheenColor * (D_Charlie(noh, RoughnessToAlpha(shading.modelData.y)) * V_Neubelt(shading.nov, specularNol) * specularNol);
        float beneath = saturate(1.0 - Max3(sheenColor) * shading.sheenAlbedo);
        return ((diffuse + specular) * beneath + sheen) * light.illuminance;
    }

    [branch] if (shading.model == ShadingSubsurface)
    {
        // Light wraps past the terminator, and some passes through from behind, tinted by the surface.
        float wrapped = Fd_Wrap(nolCentre, shading.modelData.y);
        float through = saturate(-nolCentre) * shading.modelData.x * InvPi;
        return (shading.diffuseColor * (wrapped + through) + specular) * light.illuminance;
    }

    return (diffuse + specular) * light.illuminance;
}

#endif
