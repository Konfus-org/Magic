// Volumetric light: the sun's light the air scatters towards the camera along each pixel's ray, from wherever the air
// sees the sun, so it streams in shafts through the gaps between what stands in its way, "intensity" times added to the
// picture. The air scatters "density" of the light a metre, "anisotropy" of it onward as the fog does, out to
// "distance" metres; each ray takes "samples" steps from a start that moves each frame, and asks the world at each how
// much of the sun's light gets through to it. The rays are marched at a quarter of the picture's size, the third level,
// blended with last frame's where each pixel's surface was then, and read back along the surfaces they end at.
module "Post/VolumetricLight";

import Core.Frame;
import Core.Random;
import Light.Indirect;
import Post.IPixelEffect;
import Scene.Lights;
import Scene.Shadows;
import Weather.Fog;

// The level of the target the rays are marched at, a quarter of the picture's size.
static const uint MarchedLevel = 2u;

// The most steps along a ray, which keeps the march short.
static const float MostSamples = 64.0;

// How much of each frame's march the blend over frames takes in.
static const float NewWeight = 0.1;

// How far, as a share of its depth, a pixel's surface may be from where last frame's stood before last frame's is
// dropped.
static const float DepthTolerance = 0.1;

public struct VolumetricLight : IPixelEffect
{
    public static const uint Levels = 3;
    public static const uint History = 1;
    public static const uint Rays = 1;

    public float intensity;
    public float density;
    public float anisotropy;
    public float distance;
    public float samples;

    // The light the air along the pixel's ray scatters towards the camera.
    float3 March(PixelEffectInputs inputs)
    {
        // As far as the nearest surface, a pane of glass included, and no farther than distance, which is all
        // where nothing was drawn, so what the shafts cross shows through a pane in its own colour.
        FrameConstants frame = GetFrame();
        Lights lights = GetLights();
        float3 direction = normalize(inputs.nearestPosition);
        float reach = min(length(inputs.nearestPosition), distance);
        float count = clamp(round(samples), 1.0, MostSamples);
        float step = reach / count;
        float start = GetRandom(inputs.pixel, asuint(frame.timeSec), 0u).x;
        float3 lit = float3(0.0);
        for (float i = 0.0; i < count; i++)
        {
            float along = (i + start) * step;
            float3 point = frame.cameraPosition.xyz + direction * along;
            lit += GetTransmittance(point, lights.sunDirection.xyz, 1e6) * exp(-density * along);
        }

        float phase = GetScatteringPhase(anisotropy, dot(direction, lights.sunDirection.xyz));
        return lights.sunIlluminance.rgb * phase * density * step * lit;
    }

    public override float4 Level(PixelEffectInputs inputs)
    {
        // The levels before it hold nothing.
        if (inputs.level != MarchedLevel)
            return float4(0.0);

        float4 marched = float4(March(inputs), inputs.viewDepth);
        if (!inputs.hasHistory)
            return marched;

        uint2 pictureSize = inputs.velocity.GetSize(0u);
        uint2 texel = min(uint2(inputs.uv * float2(pictureSize)), pictureSize - 1u);
        float4 velocity = inputs.velocity.Load(texel, 0u);
        float2 previousUv = inputs.uv + velocity.xy;
        float4 previous = inputs.history.SampleLevel(inputs.sampler, previousUv, float(MarchedLevel));
        bool isOnPicture = all(previousUv >= 0.0) && all(previousUv <= 1.0);
        bool isSameSurface = abs(previous.a - velocity.z) <= DepthTolerance * velocity.z;
        float3 blended = isOnPicture && isSameSurface ? lerp(previous.rgb, marched.rgb, NewWeight) : marched.rgb;
        return float4(blended, marched.a);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        return color + intensity * SampleMarched(inputs);
    }

    // The marched light at the pixel, from the marched texels around it on its surface, each marched from the pixel at
    // its middle, so a shaft stops at the edge of what stands in it; filtered between them where the pixel is sky or
    // none of them lies on its surface.
    float3 SampleMarched(PixelEffectInputs inputs)
    {
        float3 filtered = inputs.target.SampleLevel(inputs.sampler, inputs.uv, float(MarchedLevel)).rgb;
        if (inputs.viewDepth == 0.0)
            return filtered;

        uint2 size = inputs.target.GetSize(MarchedLevel);
        TracedTaps taps = GetTracedTaps(
            GetFrame(), size, float2(0.5), inputs.pixel, inputs.position, inputs.normal, inputs.viewDepth, 1.0);
        float weightSum = dot(taps.weights, float4(1.0));
        if (weightSum <= 0.0)
            return filtered;

        float3 sum = float3(0.0);
        for (uint i = 0u; i < 4u; i++)
        {
            sum += inputs.target.Load(taps.GetTexel(i), MarchedLevel).rgb * taps.weights[i];
        }

        return sum / weightSum;
    }
}
