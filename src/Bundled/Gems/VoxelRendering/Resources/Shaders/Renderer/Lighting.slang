// The lighting, a thread a pixel of the view: what the G-buffer says is there, lit by the world's directional lights
// and its point and spot lights (Lighting/Lights, Renderer/Lights), each shadowed by a cone traced through the voxels
// toward it (ConeShadowing), and by the sky from everywhere (Lighting/SkyLight), hidden as the occlusion pass found;
// pre-exposed by the view's exposure, with what the surface emits added. A pixel nothing was drawn on is left to the
// sky. A pixel lit to a NaN or an infinity, which every pass after this would spread, is drawn as the failure instead,
// and raised.

module "Renderer/Lighting";

import Core.Failure;
import Core.GBuffer;
import Core.Math;
import Lighting.SkyLight;
import Renderer.Lights;
import Renderer.Voxels;

// The pipeline script's LightingConstants, member for member.
struct LightingConstants
{
    GBuffer gbuffer;
    SkyLight sky;
    VoxelScene voxels;
    LightList lights;
    TextureRef occlusion;    // the occlusion pass's (Renderer/Occlusion), read where isOccluded
    uint isOccluded;
    TextureRef hdr;          // written where something was drawn
}

// Steps of the contact trace, and how thick a surface the view's depth shows is taken to be, in voxels of the finest
// level holding the point.
static const uint ContactSteps = 8u;
static const float ContactThickness = 2.0;

// A surface to shade: where it is, camera-relative, its pixel and view depth, what it is made of, and toward the
// camera.
struct ShadePoint
{
    float3 position;
    uint2 pixel;
    float viewDepth;
    float3 view;
    GBufferSurface surface;
}

// A light's shadow: a cone traced through the voxels from the surface toward the light, as wide as the light looks
// from there, ending at the light's surface, so a light inside a fixture is not hidden by it; the cone leaves the
// surface a voxel and a half off, and what that skips, a march along the view's depth covers (the contact trace).
struct ConeShadowing : IShadowing
{
    VoxelScene voxels;
    TextureRef depth;

    float3 GetVisibility(Shading shading, float3 position, LightSample light)
    {
        float3 world = position + View.cameraPosition;
        uint finest = voxels.GetFinestLevel(world);
        if (finest >= voxels.levelCount)
        {
            return float3(1.0, 1.0, 1.0);
        }

        float voxel = voxels.levels[finest].voxelSize;
        float contact = GetContactVisibility(position, light, voxel);
        if (contact <= 0.0)
        {
            return float3(0.0, 0.0, 0.0);
        }

        Cone cone;
        cone.origin = world + shading.normal * (1.5 * voxel);
        cone.direction = light.l;
        cone.normal = shading.normal;
        cone.tanHalfAngle = light.tanHalfAngle;
        cone.start = voxel;
        cone.end = light.distance;
        float open = voxels.TraceTransmittance(cone) * contact;
        return float3(open, open, open);
    }

    // Whether the view's depth shows something between the surface and the light over the first voxels the cone
    // skips: a point along the way behind what the view drew there, by less than a surface is thick, is hidden.
    float GetContactVisibility(float3 position, LightSample light, float voxel)
    {
        float reach = min(2.5 * voxel, light.distance);
        float thickness = ContactThickness * voxel;
        [loop] for (uint stepIndex = 1u; stepIndex <= ContactSteps; stepIndex++)
        {
            float3 seen = View.ToView(position + light.l * (reach * (float)stepIndex / (float)ContactSteps));
            if (seen.z <= View.near)
            {
                break;
            }

            float2 pixel = View.ToViewPixel(seen);
            if (any(pixel < 0.0) || any(pixel >= View.viewSize))
            {
                break;
            }

            float drawn = depth.Load(int2(pixel), 0).r;
            float behind = drawn > 0.0 ? seen.z - View.GetViewDepth(drawn) : 0.0;
            if (behind > voxel * 0.25 && behind < thickness)
            {
                return 0.0;
            }
        }

        return 1.0;
    }
}

// The luminance a surface sends toward the camera from the world's lights and the sky.
float3 ShadeSurface(LightingConstants c, ShadePoint at)
{
    GBufferSurface surface = at.surface;
    float nov = max(dot(surface.normal, at.view), 1e-4);
    float2 dfg = c.sky.GetDfg(nov, surface.roughness).xy;
    float sheen = 0.0;
    [branch] if (surface.model == ShadingModel.Cloth)
    {
        sheen = c.sky.GetDfg(nov, surface.modelData.y).z;
    }

    Shading shading = CreateShading(
        surface.baseColor,
        surface.metallic,
        surface.specular,
        surface.roughness,
        surface.normal,
        at.view,
        surface.model,
        surface.modelData,
        dfg,
        sheen);
    ConeShadowing shadowing = { c.voxels, c.gbuffer.depth };
    float3 luminance = ShadeLights(World.lights, shading, at.position, shadowing)
        + ShadeLocalLights(c.lights, at.pixel, at.viewDepth, shading, at.position, shadowing);

    float3 reflected = reflect(-at.view, surface.normal);
    float3 dominant = GetDominantDirection(surface.normal, reflected, surface.roughness);
    float3 prefiltered = c.sky.SampleSpecular(dominant, surface.roughness);
    float3 coatPrefiltered = float3(0.0, 0.0, 0.0);
    [branch] if (surface.model == ShadingModel.ClearCoat)
    {
        coatPrefiltered = c.sky.SampleSpecular(reflected, surface.modelData.y);
    }

    // The sky's light comes from where the voxels let it, along the bent normal; what hides it nearby and the
    // material's own occlusion darken it, and its reflections, as a crease would.
    float3 skyNormal = surface.normal;
    float skySeen = 1.0;
    float occlusion = surface.occlusion;
    [branch] if (c.isOccluded != 0u)
    {
        float4 traced = c.occlusion.Load(int2(at.pixel), 0);
        skyNormal = DecodeOctahedral(traced.xy * 2.0 - 1.0);
        occlusion = min(occlusion, traced.z);
        skySeen = traced.w;
    }

    float specularOcclusion = GetSpecularOcclusion(nov, occlusion, surface.roughness) * skySeen;
    float3 irradiance = c.sky.GetIrradiance(skyNormal) * skySeen * GetMultiBounceAo(occlusion, shading.diffuseColor);
    luminance += ShadeAmbient(shading, irradiance, prefiltered * specularOcclusion, coatPrefiltered * specularOcclusion);
    return luminance;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeLighting(uint3 id : SV_DispatchThreadID, uniform PassHeader pass, uniform LightingConstants c)
{
    BeginPass(pass);
    int2 pixel = int2(id.xy);
    if (any(id.xy >= (uint2)View.viewSize))
    {
        return;
    }

    float depth = c.gbuffer.depth.Load(pixel, 0).r;
    if (depth <= 0.0)
    {
        return;
    }

    ShadePoint at;
    at.pixel = id.xy;
    at.viewDepth = View.GetViewDepth(depth);
    at.position = View.ToWorld(View.ToViewPosition(float2(id.xy) + 0.5, at.viewDepth));
    at.view = -normalize(at.position);
    at.surface = c.gbuffer.Load(pixel);
    float3 lit = ShadeSurface(c, at) * View.exposure[0u].x + c.gbuffer.emissive.Load(pixel, 0).rgb;
    [branch] if (any(isnan(lit)) || any(isinf(lit)))
    {
        RaiseFailure(World.failures, FailureCode.NaN);
        lit = GetFailureGlow(World.clocks.realSec);
    }

    c.hdr.GetLevel(0u)[id.xy] = float4(lit, 1.0);
}
