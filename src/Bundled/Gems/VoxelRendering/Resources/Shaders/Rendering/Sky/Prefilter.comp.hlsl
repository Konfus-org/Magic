// The sky's light as a rough surface reflects it, every level of a cube in one dispatch, a thread a texel of a face of a
// level: level m holds the captured sky convolved with GGX at roughness m / (levels - 1), the view taken to be the
// normal and the reflection (Karis 2013, "Real Shading in Unreal Engine 4"), which Lighting/Ibl.hlsli's
// PrefilteredLevel reads it by. Each sample is read from the level of the captured sky as wide as its share of the lobe
// (Krivanek & Colbert 2008, "Real-time Shading with Filtered Importance Sampling"), so few samples are smooth and a
// small bright sun does not sparkle. Level 0, a mirror's, is the captured sky as it is.

#include "Include/Brdf.hlsli"
#include "Include/Cube.hlsli"
#include "Include/Heap.hlsli"
#include "Include/Noise.hlsli"

struct PrefilterConstants
{
    uint cube;               // the captured sky, with its levels
    uint sampler;            // linear
    uint specular;           // written, every level
    uint levels;             // the levels it has, roughness 0 to 1
    uint samples;            // of the lobe, for each texel
};

CONSTANTS(PrefilterConstants, c);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint level = id.z / 6u;
    uint face = id.z % 6u;
    if (level >= c.levels)
    {
        return;
    }

    uint width;
    uint height;
    uint layers;
    ARRAY_LEVEL(c.specular, level).GetDimensions(width, height, layers);
    if (any(id.xy >= uint2(width, height)))
    {
        return;
    }

    float3 normal = CubeDirection(face, (float2(id.xy) + 0.5) / float2(width, height));
    float capturedWidth;
    float capturedHeight;
    float capturedLevels;
    CUBE(c.cube).GetDimensions(0u, capturedWidth, capturedHeight, capturedLevels);
    [branch] if (level == 0u)
    {
        ARRAY_LEVEL(c.specular, 0u)[uint3(id.xy, face)] = float4(CUBE(c.cube).SampleLevel(SAMPLER_STATE(c.sampler), normal, 0.0).rgb, 1.0);
        return;
    }

    float roughness = (float)level / (float)max(c.levels - 1u, 1u);
    float alpha = RoughnessToAlpha(roughness);

    // The solid angle a texel of the captured sky's first level covers, about.
    float texelAngle = 4.0 * Pi / (6.0 * capturedWidth * capturedWidth);
    float3 sum = float3(0.0, 0.0, 0.0);
    float weights = 0.0;
    [loop] for (uint i = 0u; i < c.samples; i++)
    {
        float3 halfway = TangentToWorld(ImportanceSampleGgx(Hammersley(i, c.samples), alpha), normal);
        float noh = saturate(dot(normal, halfway));
        float3 toLight = 2.0 * noh * halfway - normal;
        float nol = dot(normal, toLight);
        [branch] if (nol <= 0.0)
        {
            continue;
        }

        // With the view the normal, n.h = v.h, and the sample's density over the sphere is D / 4.
        float sampleAngle = 4.0 / ((float)c.samples * max(D_Ggx(noh, alpha), 1e-6));
        float read = clamp(0.5 * log2(sampleAngle / texelAngle) + 1.0, 0.0, capturedLevels - 1.0);
        sum += CUBE(c.cube).SampleLevel(SAMPLER_STATE(c.sampler), toLight, read).rgb * nol;
        weights += nol;
    }

    ARRAY_LEVEL(c.specular, level)[uint3(id.xy, face)] = float4(sum / max(weights, 1e-6), 1.0);
}
