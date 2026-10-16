// The traced reflections (Reflections/ReflectionTrace.comp.hlsl) made smooth, once a pixel of the view: a rough surface's
// averaged with its neighbours' on the same surface, as near as its depth and as alike as its normal, over a wider
// patch the rougher it is, a smooth one's left sharp; then blended into what the pixel held last frame, found by its
// velocity, clipped toward what its neighbours hold now (Salvi 2016's variance clipping, in YCoCg as the upscale
// does), so what the ray drawn each frame misses is filled in over frames, and a pixel whose ray went from one thing
// to another eases across rather than jumping, without what moved trailing behind. A smooth surface keeps less of last
// frame's, as its reflection moves with the eye in a way its velocity does not say.

#include "Include/Color.hlsli"
#include "Include/Packing.hlsli"

struct PassParams
{
    float sharpest = 0.6;   // of last frame's a mirror keeps
    float steadiest = 0.95; // of last frame's a surface keeps from half as rough as can be on
    float spread = 1.5;     // standard deviations of the neighbours last frame's is clipped within
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)ViewSize))
    {
        return;
    }

    PassParams params = LoadPassParams();
    float4 centre = ReflectionTraced.Load(int3(id.xy, 0));
    [branch] if (centre.a <= 0.0)
    {
        Reflections[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float depth = Depth.Load(int3(id.xy, 0));
    float4 stored = Normal.Load(int3(id.xy, 0));
    float3 normal = OctDecode(stored.xy * 2.0 - 1.0);
    float roughness = stored.z;
    int radius = roughness > 0.3 ? 2 : roughness > 0.1 ? 1 : 0;
    float3 sum = float3(0.0, 0.0, 0.0);
    float weights = 0.0;
    float3 moment1 = float3(0.0, 0.0, 0.0);
    float3 moment2 = float3(0.0, 0.0, 0.0);
    float counted = 0.0;
    [loop] for (int y = -2; y <= 2; y++)
    {
        [loop] for (int x = -2; x <= 2; x++)
        {
            int2 at = clamp(int2(id.xy) + int2(x, y), int2(0, 0), int2(ViewSize) - 1);
            float4 traced = ReflectionTraced.Load(int3(at, 0));
            [branch] if (traced.a <= 0.0)
            {
                continue;
            }

            // The neighbours' spread, from the nine nearest, for the clip.
            [branch] if (abs(x) <= 1 && abs(y) <= 1)
            {
                float3 ycocg = RgbToYCoCg(traced.rgb);
                moment1 += ycocg;
                moment2 += ycocg * ycocg;
                counted += 1.0;
            }

            [branch] if (abs(x) > radius || abs(y) > radius)
            {
                continue;
            }

            float3 facing = OctDecode(Normal.Load(int3(at, 0)).xy * 2.0 - 1.0);
            float neighbourDepth = Depth.Load(int3(at, 0));
            float alike = pow(saturate(dot(normal, facing)), 16.0);
            float close = saturate(1.0 - abs(neighbourDepth - depth) / max(depth * 0.05, 1e-6));
            float weight = exp(-0.5 * (float)(x * x + y * y)) * alike * close;
            sum += traced.rgb * weight;
            weights += weight;
        }
    }

    float3 current = weights > 0.0 ? sum / weights : centre.rgb;
    float3 mean = moment1 / max(counted, 1.0);
    float3 deviation = sqrt(max(moment2 / max(counted, 1.0) - mean * mean, 0.0)) * params.spread;
    float2 then = (float2(id.xy) + 0.5) * ViewTexel - Velocity.Load(int3(id.xy, 0)).xy;
    float3 result = current;
    [branch] if (all(then > 0.0) && all(then < 1.0))
    {
        float4 held = ReflectionsHistory.SampleLevel(ReflectionsHistorySampler, then, 0.0);
        float keep = held.a > 0.0 ? lerp(params.sharpest, params.steadiest, saturate(roughness * 2.0)) : 0.0;

        // Last frame's moved toward the neighbours' mean until it is inside the box their spread makes.
        float3 offset = RgbToYCoCg(held.rgb) - mean;
        float3 units = abs(offset / max(deviation, 1e-5));
        float most = max(units.x, max(units.y, units.z));
        float3 clipped = YCoCgToRgb(most > 1.0 ? mean + offset / most : mean + offset);
        result = lerp(current, clipped, keep);
    }

    Reflections[id.xy] = float4(result, centre.a);
}
