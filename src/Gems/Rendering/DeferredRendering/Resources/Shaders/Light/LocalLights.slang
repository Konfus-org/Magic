// What a local light gives a point: its direction, its irradiance there in the frame's units, nothing past its reach,
// and a random point of the light for a shadow ray to aim at. A point light is a sphere, a spot light a cone of one, a
// rect light a rectangle lit from its nearest point (Karis, "Real Shading in Unreal Engine 4", 2013).
module "Light/LocalLights";

import Core.Math;
import Core.Sampling;
import Scene.Lights;

public struct LightSample
{
    // Towards the light, unit.
    public float3 direction;
    public float3 irradiance;
}

// Light falls off with the square of the distance, and to nothing at the light's reach.
float GetFalloff(float distance, float range)
{
    float ratio = distance / max(range, Epsilon);
    float window = saturate(1.0 - ratio * ratio * ratio * ratio);
    return window * window / max(distance * distance, 0.01);
}

// A light's own axes as rows: across and up its face, and the way it shines.
public float3x3 GetLightBasis(GpuLight light)
{
    float3 right = NormalizeSafe(cross(float3(0.0, 1.0, 0.0), light.direction), float3(1.0, 0.0, 0.0));
    return float3x3(right, cross(light.direction, right), light.direction);
}

// A random point of the light as a receiver sees it, from two random numbers: anywhere on a rect light's face, or on
// the disc of a point or spot light's sphere that faces the receiver.
public float3 SampleLightPoint(GpuLight light, float3 position, float2 xi)
{
    if (light.type == LightType.Rect)
    {
        float3x3 basis = GetLightBasis(light);
        float2 offset = (xi - 0.5) * float2(light.width, light.height);
        return light.position + basis[0] * offset.x + basis[1] * offset.y;
    }

    float3 toReceiver = NormalizeSafe(position - light.position, float3(0.0, 1.0, 0.0));
    return light.position + ToWorld(CreateBasis(toReceiver), float3(SampleConcentricDisk(xi) * light.radius, 0.0));
}

// A direction towards a random point of the sun's disc, from two random numbers, for a ray that makes its shadow soft.
public float3 SampleSunDirection(float3 toSun, float2 xi)
{
    return normalize(ToWorld(CreateBasis(toSun), float3(SampleConcentricDisk(xi) * SunAngularRadius, 1.0)));
}

// A shadow ray from an origin towards a point: its unit direction, and how far it goes, stopping short of the
// point so it doesn't hit what it aims at.
public struct LightRay
{
    public float3 direction;
    public float distance;
}

public LightRay CreateLightRay(float3 origin, float3 target)
{
    float3 toTarget = target - origin;
    float reach = length(toTarget);
    LightRay ray;
    ray.direction = toTarget / max(reach, Epsilon);
    ray.distance = reach * 0.999;
    return ray;
}

public LightSample SampleLight(GpuLight light, float3 position, float3 normal)
{
    LightSample sample;
    float3 from = light.position;
    float3 along = light.direction;
    if (light.type == LightType.Rect)
    {
        // The nearest point of the rectangle to the receiver, in the light's own axes.
        float3x3 basis = GetLightBasis(light);
        float3 offset = position - light.position;
        float x = clamp(dot(offset, basis[0]), -light.width * 0.5, light.width * 0.5);
        float y = clamp(dot(offset, basis[1]), -light.height * 0.5, light.height * 0.5);
        from = light.position + basis[0] * x + basis[1] * y;
    }

    // A light is never nearer than its own size, a sphere's radius or a rectangle's as a disc of its area, so it gives
    // at most what its face could rather than growing without bound at its surface.
    float3 toLight = from - position;
    float size = light.type == LightType.Rect ? sqrt(light.width * light.height / Pi) : light.radius;
    float distance = max(length(toLight), size);
    sample.direction = toLight / max(length(toLight), Epsilon);
    float spread = 1.0;
    if (light.type == LightType.Spot)
    {
        float cosine = dot(-sample.direction, along);
        spread = smoothstep(light.cosOuter, light.cosInner, cosine);
    }
    else if (light.type == LightType.Rect)
    {
        spread = saturate(dot(-sample.direction, along));
    }

    sample.irradiance = light.radiance * (GetFalloff(distance, light.range) * spread);
    return sample;
}
