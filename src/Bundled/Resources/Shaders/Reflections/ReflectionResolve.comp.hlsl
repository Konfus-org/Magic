// The traced reflections (Reflections/ReflectionTrace.comp.hlsl) made smooth, once a pixel of the view: a rough surface's
// averaged with its neighbours' on the same surface, as near as its depth and as alike as its normal, over a wider
// patch the rougher it is, a smooth one's left sharp; then blended into what the pixel held last frame, so what the ray
// drawn each frame misses is filled in over frames, without what moved trailing behind:
// - Last frame's is looked for where the reflection's image was, not the surface: the image of what the ray reflects
//   stands behind the surface, as far beyond it along the view as the ray went, so that point is found in last frame's
//   view (Stachowiak 2015, "Stochastic Screen-Space Reflections"), which follows a reflection sliding across a mirror or
//   a ball as the camera moves. A rough surface's reflection is a blur that hardly moves, so the rougher the surface,
//   the more by its own velocity.
// - It is clipped toward what the neighbours on the same surface hold now, their mean and spread in YCoCg, as the
//   upscale does (Salvi 2016), the neighbours weighed as the filter weighs them, so another surface's reflection beside
//   it does not widen the box.
// - And the further outside that box it was, the less of it is kept: noise stays inside the box and keeps filling in,
//   while a reflection that changed, a ball shot across a mirror, a light turned on, something come into sight, is
//   outside it and replaced within a frame or two.
// A smooth surface keeps less of last frame's than a rough one, as its reflection is sharp and its noise small.

#include "Include/Color.hlsli"
#include "Include/Packing.hlsli"

struct PassParams
{
    float sharpest = 0.3;   // of last frame's a mirror keeps
    float steadiest = 0.85; // of last frame's a surface keeps from a third as rough as can be on
    float spread = 1.0;     // standard deviations of the neighbours last frame's is clipped within
};

// The least spread the clip's box has, as a share of the neighbours' brightness, so a flat reflection's history is not
// thrown out for the last of its noise.
static const float LeastSpread = 0.05;

// Where a pixel's reflection was in last frame's view, as a uv: the image of what its ray reflects, distance beyond
// the surface along the view, seen through last frame's camera. pixel is the pixel's place, depth its depth.
float2 ImageThen(float2 pixel, float depth, float distance)
{
    float3 position = ViewToWorld(ViewPosition(pixel, ViewDepth(depth)));
    float3 image = position + normalize(position) * distance;
    float4 clip = mul(PrevViewProj, float4(image + CameraMotion, 1.0));
    float2 then = clip.xy / max(clip.w, 1e-6);
    float2 now = float2(pixel.x * ViewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * ViewTexel.y * 2.0) - Jitter;
    return pixel * ViewTexel - (now - then) * float2(0.5, -0.5);
}

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
    int radius = roughness > 0.3 ? 3 : roughness > 0.15 ? 2 : roughness > 0.05 ? 1 : 0;
    float3 sum = float3(0.0, 0.0, 0.0);
    float weights = 0.0;
    float3 moment1 = float3(0.0, 0.0, 0.0);
    float3 moment2 = float3(0.0, 0.0, 0.0);
    float counted = 0.0;
    [loop] for (int y = -3; y <= 3; y++)
    {
        [loop] for (int x = -3; x <= 3; x++)
        {
            [branch] if ((abs(x) > radius && abs(x) > 2) || (abs(y) > radius && abs(y) > 2))
            {
                continue;
            }

            int2 at = clamp(int2(id.xy) + int2(x, y), int2(0, 0), int2(ViewSize) - 1);
            float4 traced = ReflectionTraced.Load(int3(at, 0));
            [branch] if (traced.a <= 0.0)
            {
                continue;
            }

            // How much a neighbour is the same surface: as alike in normal and as near in depth.
            float3 facing = OctDecode(Normal.Load(int3(at, 0)).xy * 2.0 - 1.0);
            float neighbourDepth = Depth.Load(int3(at, 0));
            float alike = pow(saturate(dot(normal, facing)), 16.0);
            float close = saturate(1.0 - abs(neighbourDepth - depth) / max(depth * 0.05, 1e-6));
            float same = alike * close;

            // The same surface's spread, from the twenty-five nearest, for the clip.
            [branch] if (abs(x) <= 2 && abs(y) <= 2)
            {
                float3 ycocg = RgbToYCoCg(traced.rgb);
                moment1 += ycocg * same;
                moment2 += ycocg * ycocg * same;
                counted += same;
            }

            [branch] if (abs(x) > radius || abs(y) > radius)
            {
                continue;
            }

            float weight = exp(-0.5 * (float)(x * x + y * y)) * same;
            sum += traced.rgb * weight;
            weights += weight;
        }
    }

    float3 current = weights > 0.0 ? sum / weights : centre.rgb;
    float3 mean = moment1 / max(counted, 1e-4);
    float3 deviation = sqrt(max(moment2 / max(counted, 1e-4) - mean * mean, 0.0)) * params.spread;
    deviation = max(deviation, max(abs(mean.x) * LeastSpread, 1e-5));

    // Where the reflection was: its image's place for a smooth surface, the surface's own for a rough one.
    float2 pixel = float2(id.xy) + 0.5;
    float2 surfaceThen = pixel * ViewTexel - Velocity.Load(int3(id.xy, 0)).xy;
    float2 imageThen = ImageThen(pixel, depth, ReflectionDistance.Load(int3(id.xy, 0)));
    float2 then = lerp(surfaceThen, imageThen, saturate(1.0 - roughness * 3.0));
    float3 result = current;
    [branch] if (all(then > 0.0) && all(then < 1.0))
    {
        float4 held = ReflectionsHistory.SampleLevel(ReflectionsHistorySampler, then, 0.0);
        float keep = held.a > 0.0 ? lerp(params.sharpest, params.steadiest, saturate(roughness * 3.0)) : 0.0;

        // Last frame's moved toward the neighbours' mean until it is inside the box their spread makes, and kept the
        // less the further outside it was.
        float3 offset = RgbToYCoCg(held.rgb) - mean;
        float3 units = abs(offset / deviation);
        float most = max(units.x, max(units.y, units.z));
        float3 clipped = YCoCgToRgb(most > 1.0 ? mean + offset / most : mean + offset);
        keep /= max(most * most, 1.0);
        result = lerp(current, clipped, keep);
    }

    Reflections[id.xy] = float4(result, centre.a);
}
