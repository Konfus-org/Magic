// The light that reaches a ray's hit from the lights: one picked at random, the sun among them, with a ray to say
// nothing is in the way.
module "Trace/HitLight";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Sampling;
import Scene.Lighting;
import Scene.Lights;
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
    if (cosine <= 0.0 || !CanReach(world, origin, direction, distance))
        return float3(0.0);

    return float(lightCount + 1u) * irradiance * cosine * InvPi;
}
