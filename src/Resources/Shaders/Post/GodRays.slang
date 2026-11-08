// God rays: the sun's light through the gaps between what stands before it, streaking out from it, "intensity" times
// added to the picture. The first level is the sky the camera sees around the sun, where nothing is drawn; the second,
// at half the size, that blurred along the line from each texel to the sun, "samples" reads each weighing "decay" times
// the one before, so the gaps' light stretches away from the sun. The rays fade out as the sun leaves the picture. A
// technique from Mitchell, "Volumetric Light Scattering as a Post-Process", GPU Gems 3, 2007.
module "Post/GodRays";

import Post.IPixelEffect;

// The most reads along a ray, which keeps the blur's pass short.
static const float MostSamples = 64.0;

// How tightly the sky's glow gathers around the sun: the power of the cosine from it.
static const float Glow = 256.0;

// How far off the picture's edge, in pictures, the sun may go before its rays are gone.
static const float OffPictureFade = 0.5;

public struct GodRays : IPixelEffect
{
    public static const uint Levels = 2;

    public float intensity;
    public float decay;
    public float samples;

    public override float4 Level(PixelEffectInputs inputs)
    {
        if (inputs.sunFacing <= 0.0)
            return float4(0.0);

        if (inputs.level == 0u)
        {
            bool isSky = inputs.viewDepth == 0.0;
            float cosine = saturate(dot(normalize(inputs.position), inputs.sunDirection));
            return float4(isSky ? inputs.sunRadiance * pow(cosine, Glow) : float3(0.0), 1.0);
        }

        float count = clamp(round(samples), 1.0, MostSamples);
        float2 step = (inputs.sunUv - inputs.uv) / count;
        float2 uv = inputs.uv;
        float weight = 1.0;
        float3 sum = float3(0.0);
        for (float i = 0.0; i < count; i++)
        {
            uv += step;
            sum += inputs.target.SampleLevel(inputs.sampler, uv, 0.0).rgb * weight;
            weight *= decay;
        }

        return float4(sum / count, 1.0);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        if (inputs.sunFacing <= 0.0)
            return color;

        float2 offPicture = max(max(-inputs.sunUv, inputs.sunUv - 1.0), 0.0);
        float fade = saturate(1.0 - max(offPicture.x, offPicture.y) / OffPictureFade);
        return color + intensity * fade * inputs.target.SampleLevel(inputs.sampler, inputs.uv, 1.0).rgb;
    }
}
