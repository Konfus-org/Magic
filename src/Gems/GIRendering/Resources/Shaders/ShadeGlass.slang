// The nearest translucent surface of each pixel shaded over the lit picture: what is behind it, as shading left it,
// through the pane's colour, and through any other pane between, which a ray finds on a GPU that traces rays; what it
// reflects of the light around it, more of it at a grazing angle (Schlick's Fresnel); and the sun on it, its
// highlight and what its body scatters. The entry is specialised to one translucent material shader, numbered in the
// pass header's w, and to the sky; the header's x is the frame's buffer, which holds the lights, and y the probe
// grid's constants, or 0 where there are none.
module "ShadeGlass";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Instances;
import Scene.Materials;
import Scene.Visibility;
import Scene.Lights;
import Scene.Probes;
import Surface.Surface;
import Surface.Brdf;
import Material.IMaterial;
import Sky.ISky;
import Trace.HitSurface;
import Trace.HitLight;
import Scene.Clusters;

// How much of the light glass reflects straight on: an index of refraction of 1.5.
static const float GlassReflectance = 0.04;

// The light reaching a point from a direction: the probes' where there are any, else the sky's.
float3 GetSurroundings<S : ISky>(Lights lights, float3 world, float3 direction)
{
    if (GetPassHeader().y != 0u)
        return GetProbeIrradiance(GetProbeGrid(), world, direction);

    return GetSkyRadiance<S>(lights, direction);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeGlass<M : IMaterial, S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    UintTextureRef glass = { GetClusters().glassVisibility };
    uint visibleIndex;
    uint triangle;
    if (!DecodeVisibilityId(glass.Load(thread.xy, 0u), visibleIndex, triangle))
        return;

    float2 center = GetTexelCenterUv(thread.xy, size);
    PixelTriangle shown = LoadPixelTriangle(frame, visibleIndex, triangle, ToClip(center));
    uint row = GetMaterial(shown.instance, shown.cluster.materialSlot);
    if (LoadMaterialShader(frame.materials, row) != GetPassHeader().w)
        return;

    SurfaceInputs inputs = CreateSurfaceInputs(shown);
    M material = LoadMaterialParams<M>(frame.materials, row);
    SurfaceOutputs surface = material.Evaluate(inputs);
    Lights lights = GetLights();
    float3 view = inputs.viewDirection;
    // A pane is seen from either side.
    float3 normal = dot(surface.normal, view) < 0.0 ? -surface.normal : surface.normal;
    float3 world = inputs.position + frame.cameraPosition.xyz;
    float fresnel = GlassReflectance + (1.0 - GlassReflectance) * Pow5(1.0 - saturate(dot(normal, view)));

    // What is behind, through the pane and any other between it and the opaque surface.
    StorageRef picture = { frame.hdr, 0u };
    float3 through = surface.baseColor * (1.0 - surface.alpha);
    float3 toSun = lights.sunDirection.xyz;
    float3 sunIlluminance = lights.hasSky != 0u
        ? S.GetSunIlluminance(CreateSkyInputs(lights, toSun, false))
        : lights.sunIlluminance.rgb;
    if (frame.acceleration != 0u)
    {
        AccelerationRef acceleration = { frame.acceleration };
        RaytracingAccelerationStructure scene = acceleration.Get();
        TextureRef depth = { frame.depth };
        float3 opaque = ReconstructRelative(frame, center, max(depth.Load(thread.xy, 0u).x, 1e-6));
        float offset = GetRayOffset(frame, inputs.position);
        float behind = length(opaque - inputs.position) * 0.999;
        through *= GetTransmittance(frame, scene, world - view * offset, -view, behind);
        float3 sunSide = dot(normal, toSun) > 0.0 ? normal : -normal;
        sunIlluminance *= GetTransmittance(frame, scene, world + sunSide * offset, toSun, 1e6);
    }

    Brdf body = CreateBrdf(surface.baseColor * surface.alpha, 0.0, surface.roughness, 0.5);
    float3 color = picture.Load(thread.xy).rgb * through * (1.0 - fresnel)
        + fresnel * GetSurroundings<S>(lights, world, reflect(-view, normal))
        + body.diffuse * GetSurroundings<S>(lights, world, normal)
        + Evaluate(body, normal, view, toSun) * sunIlluminance
        + surface.emissive;
    picture.Store(thread.xy, float4(color, 1.0));
}
