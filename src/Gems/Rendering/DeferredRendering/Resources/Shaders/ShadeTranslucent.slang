// The nearest translucent surface of each pixel shaded over the lit picture: what is behind it, as shading left it and
// seen through the air between them, through the pane's colour and through any other pane between, which a ray finds
// on a GPU that traces rays, what it mirrors, more of it at a grazing angle (Schlick's Fresnel), which one ray traces on
// such a GPU and which is the sky elsewhere, and the sun's highlight on it. Then the air in front of it, or in front of
// the opaque surface where no pane is. Each pane's surface is what the resolve left in the frame's glass targets, as
// its material decided it, and where it is the glass depth. The entry is specialised to the sky. The pass header's x
// is the frame's buffer, which holds the lights, the probe grid's constants, whose probes are 0 where there are none,
// and the air.
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

// How far a pane's mirror ray reaches, in metres.
static const float MirrorRange = 10000.0;

// The light a smooth pane mirrors from origin along a direction: the sky's where the ray lands on nothing, else what
// the surface it lands on sends back, lit by the sun and the probes' light, as the reflections gem shades its hits but
// without the local lights, which only the probes bring. Each passes any glass between.
float3 GetMirrored<S : ISky>(Lights lights, float3 origin, float3 direction)
{
    RaytracingAccelerationStructure scene = GetTracedAcceleration();
    RayHit hit = GetClosestHit(scene, origin, direction, MirrorRange, RAY_FLAG_CULL_BACK_FACING_TRIANGLES);
    if (!hit.isHit)
        return GetSkyRadiance<S>(lights, direction) * hit.through;

    HitSurface landed = LoadHitSurface(hit, origin, direction);
    float3 toSun = lights.sunDirection.xyz;
    float sunCosine = dot(landed.normal, toSun);
    float3 sun = sunCosine > 0.0
        ? GetSunIlluminance<S>(lights) * sunCosine * InvPi
            * GetTransmittance(scene, landed.position + landed.normal * 0.01, toSun, 1e6)
        : float3(0.0);
    ProbeGridConstants grid = GetProbeGrid();
    float3 surroundings = grid.probes != 0u ? GetProbeIrradiance(grid, landed.position, landed.normal) : float3(0.0);
    return ((sun + surroundings) * landed.diffuse + landed.emissive) * hit.through;
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
    TextureRef depth = { frame.depth };
    float3 opaque = ReconstructPixel(frame, thread.xy, max(depth.Load(thread.xy, 0u).x, FarDepth));
    if (traced.acceleration != 0u)
    {
        RaytracingAccelerationStructure scene = GetTracedAcceleration();
        float offset = GetRayOffset(frame, position);
        // The ray ends a ray offset short of the opaque surface, where the traced surface may stand nearer than the
        // drawn one.
        float behind = max(length(opaque - position) - offset - GetRayOffset(frame, opaque), 0.0);
        through *= GetTransmittance(scene, world - view * offset, -view, behind);
        float3 sunSide = dot(normal, toSun) > 0.0 ? normal : -normal;
        sunIlluminance *= GetTransmittance(scene, world + sunSide * offset, toSun, 1e6);
    }

    float3 mirror = reflect(-view, normal);
    float3 reflected = traced.acceleration != 0u
        ? GetMirrored<S>(lights, world + normal * GetRayOffset(frame, position), mirror)
        : GetSkyRadiance<S>(lights, mirror);
    // What does not get through the pane its body absorbs: clear glass scatters none of it.
    Brdf body = CreateBrdf(float3(0.0), 0.0, surface.x, 0.5);
    float3 pane = fresnel * reflected
        + Evaluate(body, normal, view, toSun) * sunIlluminance
        + surface.yzw;
    // What is behind reaches the pane through the air between them, and the camera through the air in front of the
    // pane, whose own light, ApplyAir of nothing at the pane, the pane does not dim.
    float3 seenBehind = ApplyAir(picture.Load(thread.xy).rgb, opaque) - ApplyAir(float3(0.0), position);
    float3 color = ApplyAir(pane, position) + seenBehind * through * (1.0 - fresnel);
    picture.Store(thread.xy, float4(color, 1.0));
}
