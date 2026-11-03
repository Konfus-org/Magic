// What a local light gives a point: its direction, its irradiance there in the frame's units, and nothing past its
// reach. A point light is a sphere, a spot light a cone of one, a rect light a rectangle lit from its nearest point
// (Karis, "Real Shading in Unreal Engine 4", 2013).
module "Light/Lights";

import Core.Math;
import Scene.Lights;

public struct LightSample
{
    // Towards the light, unit.
    public float3 direction;
    public float3 irradiance;
}

// Light falls off with the square of the distance, and to nothing at the light's reach.
float Falloff(float distance, float range)
{
    float ratio = distance / max(range, Epsilon);
    float window = saturate(1.0 - ratio * ratio * ratio * ratio);
    return window * window / max(distance * distance, 0.01);
}

public LightSample SampleLight(GpuLight light, float3 position, float3 normal)
{
    LightSample sample;
    float3 from = light.position;
    float3 along = light.direction;
    if (light.type == LightRect)
    {
        // The nearest point of the rectangle to the receiver, in the light's own axes.
        float3 right = NormalizeSafe(cross(float3(0.0, 1.0, 0.0), along), float3(1.0, 0.0, 0.0));
        float3 up = cross(along, right);
        float3 offset = position - light.position;
        float x = clamp(dot(offset, right), -light.width * 0.5, light.width * 0.5);
        float y = clamp(dot(offset, up), -light.height * 0.5, light.height * 0.5);
        from = light.position + right * x + up * y;
    }

    float3 toLight = from - position;
    float distance = max(length(toLight), light.radius);
    sample.direction = toLight / max(length(toLight), Epsilon);
    float spread = 1.0;
    if (light.type == LightSpot)
    {
        float cosine = dot(-sample.direction, along);
        spread = smoothstep(light.cosOuter, light.cosInner, cosine);
    }
    else if (light.type == LightRect)
    {
        spread = saturate(dot(-sample.direction, along));
    }

    sample.irradiance = light.radiance * (Falloff(distance, light.range) * spread);
    return sample;
}
