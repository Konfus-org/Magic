// The light a ray brings back: the sky's where it hits nothing, and where it hits, what the surface there reflects of
// one light picked at random, the sun among them, with a ray to say how much of it gets through, and of the probes'
// light. The pass header's x is the frame's constants and z the lighting constants.
module "Trace/HitLight";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Sampling;
import Scene.Lighting;
import Scene.Lights;
import Scene.Probes;
import Light.Lights;
import Sky.ISky;
import Trace.HitSurface;

// The light that reaches a surface from one light picked at random by xi.x, the sun among them, aimed at a point of it
// by xi.yz, over pi and times how many there are to pick from: what a white diffuse surface reflects of them all, on
// average.
public float3 GetDirectLight<S : ISky>(
    FrameConstants frame,
    LightingConstants lighting,
    RaytracingAccelerationStructure world,
    HitSurface surface,
    float3 xi
)
{
    uint lightCount = min(lighting.lightCount, MaxLights);
    uint picked = min(uint(xi.x * float(lightCount + 1u)), lightCount);
    float3 origin = surface.position + surface.normal * 0.01;
    float3 direction;
    float3 irradiance;
    float distance;
    if (picked == lightCount)
    {
        float3 toSun = frame.sunDirection.xyz;
        direction = normalize(ToWorld(CreateBasis(toSun), float3(SampleConcentricDisk(xi.yz) * SunAngularRadius, 1.0)));
        irradiance = lighting.hasSky != 0u
            ? S.GetSunIlluminance(CreateSkyInputs(frame, lighting, toSun, false))
            : lighting.sunIlluminance.rgb;
        distance = 1e6;
    }
    else
    {
        GpuLight light = LoadLight(lighting.lights, picked);
        float3 toTarget = SampleLightPoint(light, surface.position, xi.yz) - origin;
        distance = length(toTarget) * 0.999;
        direction = toTarget / max(length(toTarget), Epsilon);
        irradiance = SampleLight(light, surface.position, surface.normal).irradiance;
    }

    float cosine = dot(surface.normal, direction);
    if (cosine <= 0.0)
        return float3(0.0);

    float3 through = GetTransmittance(frame, world, origin, direction, distance);
    return float(lightCount + 1u) * irradiance * through * cosine * InvPi;
}

// The sky along a direction, without the sun's disc, whose light the sun's own rays bring.
public float3 GetSkyRadiance<S : ISky>(FrameConstants frame, LightingConstants lighting, float3 direction)
{
    if (lighting.hasSky == 0u)
        return frame.skyRadiance.rgb * lighting.sunIlluminance.r;

    return S.Evaluate(CreateSkyInputs(frame, lighting, direction, false));
}

// The light a surface a ray landed on sends back along it: what it reflects of one light picked at random by xi and of
// the probes' light around it, and what it gives off.
public float3 GetHitLight<S : ISky>(
    FrameConstants frame,
    LightingConstants lighting,
    ProbeGridConstants grid,
    RaytracingAccelerationStructure world,
    HitSurface surface,
    float3 xi
)
{
    float3 light = GetDirectLight<S>(frame, lighting, world, surface, xi)
        + GetProbeIrradiance(grid, surface.position, surface.normal);
    return light * surface.diffuse + surface.emissive;
}
