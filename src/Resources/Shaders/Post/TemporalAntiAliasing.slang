// Temporal anti-aliasing: each pixel's colour blended with what the last frame resolved where its surface was, so the
// camera's jitter, a different point in the pixel each frame, adds up to smooth edges over frames. The history is held
// to the range of the pixel's 3 by 3 neighbours, so light that changed doesn't trail, and left out where the last frame
// saw another surface there or the surface was off the screen. Adapted from Wicked Engine's temporal AA. The resolved
// picture is the effect's one level, so it is next frame's history; each texel keeps its view depth in alpha, 0 for
// the sky, for the next frame's test.
module "Post/TemporalAntiAliasing";

import Core.Heap;
import Core.Math;
import Core.Color;
import Post.IPixelEffect;

// How far in metres the surface the history saw may be from where the pixel's surface was before it is another.
static const float SurfaceGapMetres = 1.0;

// How far a pixel's surface moves across the screen in a frame, in texture coordinates, before the history can be of
// another surface.
static const float MovingDistance = 0.01;

// The most a 16-bit float holds, which the picture is kept under.
static const float HalfMax = 65504.0;

// What the blend's exposure brings the 3 by 3 neighbours' average to.
static const float MiddleGrey = 0.18;

public struct TemporalAntiAliasing : IPixelEffect
{
    public static const uint Levels = 1;
    public static const uint Jitters = 1;

    public override float4 Level(PixelEffectInputs inputs)
    {
        // The range of the 3 by 3 neighbours' colours and their sum, and the nearest of them, whose velocity the pixel
        // takes so an edge moves with what is in front of it.
        float3 sum = float3(0.0);
        float3 low = float3(HalfMax);
        float3 high = float3(-HalfMax);
        float nearestDepth = 0.0;
        uint2 nearest = inputs.pixel;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                uint2 neighbour = uint2(clamp(int2(inputs.pixel) + int2(x, y), int2(0), int2(inputs.size) - 1));
                float3 color = inputs.picture.Load(neighbour, 0u).rgb;
                sum += color;
                low = min(low, color);
                high = max(high, color);
                float depth = inputs.depth.Load(neighbour, 0u).x;
                if (depth > nearestDepth)
                {
                    nearestDepth = depth;
                    nearest = neighbour;
                }
            }
        }

        float3 current = inputs.picture.Load(inputs.pixel, 0u).rgb;
        float viewDepth = min(inputs.viewDepth, HalfMax);
        if (!inputs.hasHistory)
            return float4(current, viewDepth);

        float2 velocity = inputs.velocity.Load(nearest, 0u).xy;
        float2 historyUv = inputs.uv + velocity;
        bool isOnScreen = all(saturate(historyUv) == historyUv);
        historyUv = saturate(historyUv);

        // Where the surface moved and the history saw another one where it was, the pixel as it is now.
        float historyDepth = inputs.history.Load(min(uint2(historyUv * float2(inputs.size)), inputs.size - 1u), 0u).a;
        float previousDepth = inputs.velocity.Load(inputs.pixel, 0u).z;
        if (length(velocity) > MovingDistance && abs(previousDepth - historyDepth) > SurfaceGapMetres)
            return float4(current, viewDepth);

        // The history read between texels, which blurs, so the more the pixel moved off whole texels the more of now
        // it takes; all of now where its surface was off the screen.
        float3 before = clamp(inputs.history.SampleLevel(inputs.sampler, historyUv, 0.0).rgb, low, high);
        float2 moved = abs(velocity) * float2(inputs.size);
        float blend = isOnScreen ? lerp(0.05, 0.8, frac(max(moved.x, moved.y)) * 0.5) : 1.0;
        float exposure = MiddleGrey / max(GetLuminance(sum / 9.0), Epsilon);
        float3 resolved = FromBlendSpace(
            lerp(ToBlendSpace(before, exposure), ToBlendSpace(current, exposure), blend),
            exposure
        );
        return float4(min(resolved, HalfMax), viewDepth);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        return inputs.target.Load(inputs.pixel, 0u).rgb;
    }
}

// A colour in Reinhard's curve after an exposure that brings its neighbours to a middle grey, where the blend is done,
// so a bright pixel doesn't outweigh the neighbours it is averaged with, wherever the effect is in the list; and back.
float3 ToBlendSpace(float3 color, float exposure)
{
    float3 exposed = color * exposure;
    return exposed / (exposed + 1.0);
}

float3 FromBlendSpace(float3 color, float exposure) { return color / max(1.0 - color, Epsilon) / exposure; }
