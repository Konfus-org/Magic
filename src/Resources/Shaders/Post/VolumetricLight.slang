// Volumetric light: the sun's light the air scatters towards the camera along each pixel's ray, from wherever the air
// sees the sun, so it streams in shafts through the gaps between what stands in its way, "intensity" times added to the
// picture. The air scatters "density" of the light a metre, "anisotropy" of it onward as the fog does, out to
// "distance" metres; each ray takes "samples" steps from a start that moves each frame, and asks the world at each how
// much of the sun's light gets through to it. The rays are marched at half the picture's size, the second level, and
// blended with last frame's where each pixel's surface was then.
module "Post/VolumetricLight";

import Core.Random;
import Post.IPixelEffect;
import Weather.Fog;

// The most steps along a ray, which keeps the march short.
static const float MostSamples = 64.0;

// How much of each frame's march the blend over frames takes in.
static const float NewWeight = 0.1;

// How far, as a share of its depth, a pixel's surface may be from where last frame's stood before last frame's is
// dropped.
static const float DepthTolerance = 0.1;

public struct VolumetricLight : IPixelEffect
{
    public static const uint Levels = 2;
    public static const uint Rays = 1;

    public float intensity;
    public float density;
    public float anisotropy;
    public float distance;
    public float samples;

    // The light the air along the pixel's ray scatters towards the camera.
    float3 March(PixelEffectInputs inputs)
    {
        float3 direction = normalize(inputs.position);
        float reach = min(inputs.viewDepth > 0.0 ? length(inputs.position) : distance, distance);
        float count = clamp(round(samples), 1.0, MostSamples);
        float step = reach / count;
        float start = GetRandom(inputs.pixel, asuint(inputs.time), 0u).x;
        float3 lit = float3(0.0);
        for (float i = 0.0; i < count; i++)
        {
            float along = (i + start) * step;
            float3 point = inputs.cameraPosition + direction * along;
            lit += WorldRays.GetTransmittance(point, inputs.sunDirection, 1e6) * exp(-density * along);
        }

        float phase = GetScatteringPhase(anisotropy, dot(direction, inputs.sunDirection));
        return inputs.sunRadiance * phase * density * step * lit;
    }

    public override float4 Level(PixelEffectInputs inputs)
    {
        // The first level, the picture's size, holds nothing: the march runs at half.
        if (inputs.level == 0u)
            return float4(0.0);

        float4 marched = float4(March(inputs), inputs.viewDepth);
        if (!inputs.hasHistory)
            return marched;

        uint2 pictureSize = inputs.velocity.GetSize(0u);
        uint2 texel = min(uint2(inputs.uv * float2(pictureSize)), pictureSize - 1u);
        float4 velocity = inputs.velocity.Load(texel, 0u);
        float2 previousUv = inputs.uv + velocity.xy;
        float4 previous = inputs.history.SampleLevel(inputs.sampler, previousUv, 1.0);
        bool isOnPicture = all(previousUv >= 0.0) && all(previousUv <= 1.0);
        bool isSameSurface = abs(previous.a - velocity.z) <= DepthTolerance * velocity.z;
        float3 blended = isOnPicture && isSameSurface ? lerp(previous.rgb, marched.rgb, NewWeight) : marched.rgb;
        return float4(blended, marched.a);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        return color + intensity * inputs.target.SampleLevel(inputs.sampler, inputs.uv, 1.0).rgb;
    }
}
