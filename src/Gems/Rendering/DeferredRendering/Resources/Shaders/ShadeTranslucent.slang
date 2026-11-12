// The nearest translucent surface of each pixel shaded over the lit picture: what is behind it, as shading left it,
// through the pane's colour, and through any other pane between, which a ray finds on a GPU that traces rays; what it
// reflects of the light around it, more of it at a grazing angle (Schlick's Fresnel); and the sun on it, its
// highlight and what its body scatters; then the air in front of it, or in front of the opaque surface where no pane
// is. Each pane's surface is what the resolve left in the frame's glass targets, as its material decided it, and where
// it is the glass depth; the entry is specialised to the sky. The pass header's x is the frame's buffer, which holds
// the lights, the probe grid's constants, whose probes are 0 where there are none, and the air.
module "ShadeTranslucent";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Light.Probes;
import Light.Indirect;
import Light.Air;
import Surface.Brdf;
import Sky.ISky;
import Trace.HitSurface;
import Scene.Models;

// How much of the light a pane reflects straight on: an index of refraction of 1.5.
static const float PaneReflectance = 0.04;

// The light reaching a point from a direction: the probes' where there are any, else the sky's.
float3 GetSurroundings<S : ISky>(Lights lights, float3 world, float3 direction)
{
    ProbeGridConstants grid = GetProbeGrid();
    if (grid.probes != 0u)
        return GetProbeIrradiance(grid, world, direction);

    return GetSkyRadiance<S>(lights, direction);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeTranslucent<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    // Where no pane is in front of the opaque surface, the glass depth is cleared to nothing, or the view has none, its
    // id 0, and the picture is only seen through the air, where something filled it, to the surface or far along the
    // ray where nothing was drawn.
    TextureRef glassDepth = { frame.glassDepth };
    float paneDepth = frame.glassDepth == 0u ? 0.0 : glassDepth.Load(thread.xy, 0u).x;
    StorageRef picture = { frame.hdr, 0u };
    if (paneDepth == 0.0)
    {
        if (GetAir().volume == 0u)
            return;

        TextureRef opaqueDepth = { frame.depth };
        float3 seen = ReconstructPixel(frame, thread.xy, max(opaqueDepth.Load(thread.xy, 0u).x, FarDepth));
        picture.Store(thread.xy, float4(ApplyAir(picture.Load(thread.xy).rgb, seen), 1.0));
        return;
    }

    TextureRef bases = { frame.glassBase };
    TextureRef surfaces = { frame.glassSurface };
    float4 base = bases.Load(thread.xy, 0u);
    float4 surface = surfaces.Load(thread.xy, 0u);
    float3 position = ReconstructPixel(frame, thread.xy, paneDepth);
    float3 view = NormalizeSafe(-position, float3(0.0, 0.0, -1.0));
    float3 paneNormal = LoadNormal(frame.glassNormal, thread.xy);
    Lights lights = GetLights();
    TracedWorld traced = GetTracedWorld();
    // A pane is seen from either side.
    float3 normal = dot(paneNormal, view) < 0.0 ? -paneNormal : paneNormal;
    float3 world = position + frame.cameraPosition.xyz;
    float fresnel = PaneReflectance + (1.0 - PaneReflectance) * Pow5(1.0 - saturate(dot(normal, view)));

    // What is behind, through the pane and any other between it and the opaque surface.
    float3 through = base.rgb * (1.0 - base.a);
    float3 toSun = lights.sunDirection.xyz;
    float3 sunIlluminance = GetSunIlluminance<S>(lights);
    if (traced.acceleration != 0u)
    {
        RaytracingAccelerationStructure scene = GetTracedAcceleration();
        TextureRef depth = { frame.depth };
        float3 opaque = ReconstructPixel(frame, thread.xy, max(depth.Load(thread.xy, 0u).x, FarDepth));
        float offset = GetRayOffset(frame, position);
        float behind = length(opaque - position) * 0.999;
        through *= GetTransmittance(scene, world - view * offset, -view, behind);
        float3 sunSide = dot(normal, toSun) > 0.0 ? normal : -normal;
        sunIlluminance *= GetTransmittance(scene, world + sunSide * offset, toSun, 1e6);
    }

    Brdf body = CreateBrdf(base.rgb * base.a, 0.0, surface.x, 0.5);
    float3 color = picture.Load(thread.xy).rgb * through * (1.0 - fresnel)
        + fresnel * GetSurroundings<S>(lights, world, reflect(-view, normal))
        + body.diffuse * GetSurroundings<S>(lights, world, normal)
        + Evaluate(body, normal, view, toSun) * sunIlluminance
        + surface.yzw;
    picture.Store(thread.xy, float4(ApplyAir(color, position), 1.0));
}
