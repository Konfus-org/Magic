// Reading shadow maps. A shadow map holds reverse-Z depth from the light, so its comparison sampler compares
// GreaterOrEqual: a receiver is lit where it is no farther from the light than the nearest caster.

#ifndef MAGIC_SHADOWS_HLSLI
#define MAGIC_SHADOWS_HLSLI

// A 5 x 5 tent of comparisons around uv (0..1 across a map of size texels) gathered from 9 bilinear comparison taps
// (Castano 2013, "Shadow Mapping Summary"): soft, even edges that do not swim as the receiver moves. The taps' places,
// and their weights, which sum to 144.
void ShadowTent(float2 uv, float2 size, out float2 taps[9], out float weights[9])
{
    float2 texel = uv * size;
    float2 centre = floor(texel + 0.5);
    float2 offset = texel + 0.5 - centre;
    float2 origin = (centre - 0.5) / size;

    float3 weightsU = float3(4.0 - 3.0 * offset.x, 7.0, 1.0 + 3.0 * offset.x);
    float3 tapsU = float3((3.0 - 2.0 * offset.x) / weightsU.x - 2.0, (3.0 + offset.x) / weightsU.y, offset.x / weightsU.z + 2.0);
    float3 weightsV = float3(4.0 - 3.0 * offset.y, 7.0, 1.0 + 3.0 * offset.y);
    float3 tapsV = float3((3.0 - 2.0 * offset.y) / weightsV.x - 2.0, (3.0 + offset.y) / weightsV.y, offset.y / weightsV.z + 2.0);
    [unroll] for (uint row = 0u; row < 3u; row++)
    {
        [unroll] for (uint column = 0u; column < 3u; column++)
        {
            taps[row * 3u + column] = origin + float2(tapsU[column], tapsV[row]) / size;
            weights[row * 3u + column] = weightsU[column] * weightsV[row];
        }
    }
}

// The share of light reaching a receiver at uv and depth through a shadow map of size texels a side, such as an atlas
// whose tiles keep their taps inside them.
float ShadowPcf(Texture2D<float> map, SamplerComparisonState compare, float2 uv, float depth, float2 size)
{
    float2 taps[9];
    float weights[9];
    ShadowTent(uv, size, taps, weights);
    float lit = 0.0;
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        lit += weights[i] * map.SampleCmpLevelZero(compare, taps[i], depth);
    }

    return lit / 144.0;
}

// Where a receiver looks itself up in a shadow map: pushed out along its normal by a texel's world size, the more the
// more the light grazes it, so a surface does not shadow itself (acne) and shadows stay on their casters (Holbert
// 2011, "Normal Offset Shadows"). toLight is unit length.
float3 NormalOffset(float3 position, float3 normal, float3 toLight, float texelWorldSize)
{
    float nol = saturate(dot(normal, toLight));
    return position + normal * (texelWorldSize * sqrt(1.0 - nol * nol));
}

#endif
