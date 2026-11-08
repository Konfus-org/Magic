// Bloom: light brighter than "threshold" after the exposure spread around where it is, as a lens scatters it, and
// "strength" times the spread added to the picture. The first level is what is over the threshold, each level after
// it the one before blurred to half its size; the picture adds the coarser levels back. Adapted from Wicked Engine's
// bloom.
module "Post/Bloom";

import Post.IPixelEffect;

// The level the picture adds back first: finer levels only blur a texel or two.
static const uint FinestAdded = 2;

// The brightest a colour blooms as, after the exposure, which keeps a lone glint from swamping the bloom.
static const float MostBright = 10.0;

// How far from a texel's middle, in texels of the level before, a downsample's four reads are: three quarters, so
// each read blends its two texels 1 to 3 and the four weigh the 4 by 4 texels under the texel as a tent.
static const float TentReach = 0.75;

public struct Bloom : IPixelEffect
{
    public static const uint Levels = 7;

    public float threshold;
    public float strength;

    public override float4 Level(PixelEffectInputs inputs)
    {
        if (inputs.level == 0u)
        {
            float3 color = inputs.picture.Load(inputs.pixel, 0u).rgb * inputs.exposure;
            return float4(max(min(color, MostBright) - threshold, 0.0) / inputs.exposure, 1.0);
        }

        uint before = inputs.level - 1u;
        float2 reach = TentReach / float2(inputs.target.GetSize(before));
        float3 sum = float3(0.0);
        for (int y = -1; y <= 1; y += 2)
        {
            for (int x = -1; x <= 1; x += 2)
                sum += inputs.target.SampleLevel(inputs.sampler, inputs.uv + float2(x, y) * reach, float(before)).rgb;
        }

        return float4(sum / 4.0, 1.0);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        float3 spread = float3(0.0);
        for (uint level = FinestAdded; level < Levels; level++)
            spread += inputs.target.SampleLevel(inputs.sampler, inputs.uv, float(level)).rgb;

        return color + strength * spread / float(Levels - FinestAdded) * inputs.exposure;
    }
}
