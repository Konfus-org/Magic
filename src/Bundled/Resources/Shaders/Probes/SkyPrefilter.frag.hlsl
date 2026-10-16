// The sky's light as a rough surface reflects it, one mip of SkySpecular a pass (Passes/SkySpecular*.pass), a face each
// time the pass runs: mip m holds SkyCube convolved with GGX at roughness m / (mips - 1), the view taken to be the
// normal and the reflection (Karis 2013, "Real Shading in Unreal Engine 4"), which Lighting/Ibl.hlsli's
// PrefilteredLevel reads it by. Each sample is read from the mip of SkyCube as wide as its share of the lobe
// (Krivanek & Colbert 2008, "Real-time Shading with Filtered Importance Sampling"), so few samples are smooth and a
// small bright sun does not sparkle. Mip 0, a mirror's, is SkyCube as it is.

#include "Include/Brdf.hlsli"
#include "Include/Cube.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/Noise.hlsli"

struct PassParams
{
    uint level = 0u;     // the mip of SkySpecular drawn
    uint levels = 6u;    // the mips SkySpecular has (Passes/SkySpecular.pass), roughness 0 to 1
    uint samples = 48u;  // of the lobe, for each texel
};

float4 main(PassVaryings input) : SV_Target0
{
    PassParams params = LoadPassParams();
    float3 normal = CubeDirection(PassIteration(), input.uv);
    float width;
    float height;
    float levels;
    SkyCube.GetDimensions(0u, width, height, levels);
    [branch] if (params.level == 0u)
    {
        return float4(SkyCube.SampleLevel(SkyCubeSampler, normal, 0.0).rgb, 1.0);
    }

    float roughness = (float)params.level / (float)max(params.levels - 1u, 1u);
    float alpha = RoughnessToAlpha(roughness);

    // The solid angle a texel of SkyCube's first mip covers, about.
    float texelAngle = 4.0 * Pi / (6.0 * width * width);
    float3 sum = float3(0.0, 0.0, 0.0);
    float weights = 0.0;
    [loop] for (uint i = 0u; i < params.samples; i++)
    {
        float3 halfway = TangentToWorld(ImportanceSampleGgx(Hammersley(i, params.samples), alpha), normal);
        float noh = saturate(dot(normal, halfway));
        float3 toLight = 2.0 * noh * halfway - normal;
        float nol = dot(normal, toLight);
        [branch] if (nol <= 0.0)
        {
            continue;
        }

        // With the view the normal, n.h = v.h, and the sample's density over the sphere is D / 4.
        float sampleAngle = 4.0 / ((float)params.samples * max(D_Ggx(noh, alpha), 1e-6));
        float level = clamp(0.5 * log2(sampleAngle / texelAngle) + 1.0, 0.0, levels - 1.0);
        sum += SkyCube.SampleLevel(SkyCubeSampler, toLight, level).rgb * nol;
        weights += nol;
    }

    return float4(sum / max(weights, 1e-6), 1.0);
}
