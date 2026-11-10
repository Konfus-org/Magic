// Temporal anti-aliasing and upscaling: each pixel of the output blended with what the last frame resolved where its
// surface was, so the camera's jitter, a different point in each of the picture's texels each frame, adds up to smooth
// edges and, where the output is larger than the picture, to detail finer than its texels. A frame's sample counts the
// more the nearer it fell to the pixel's middle. The history is held to the range of the 3 by 3 texels around it, so
// light that changed doesn't trail, and left out where the last frame saw another surface there or the surface was off
// the screen. Adapted from Wicked Engine's temporal AA. The resolved picture is the effect's one level, so it is next
// frame's history; each texel keeps its view depth in alpha, 0 for the sky, for the next frame's test.
module "Post/TemporalAntiAliasing";

import Core.Heap;
import Core.Math;
import Core.Color;
import Core.Frame;
import Post.IPixelEffect;

// How fast a sample's weight falls with its distance from the pixel's middle, in pixels: a Gaussian close to the
// Blackman-Harris window over a pixel, from Karis, "High Quality Temporal Supersampling" (SIGGRAPH 2014).
static const float SampleFalloff = 2.29;

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
        // The picture's texel whose sample this frame fell nearest the pixel's middle, as texel i sees the scene at
        // i + 0.5 - jitter, and how far from the middle that was, in the output's pixels.
        uint2 pictureSize = inputs.picture.GetSize(0u);
        float2 place = inputs.uv * float2(pictureSize) + GetFrame().jitter;
        uint2 texel = min(uint2(max(place, 0.0)), pictureSize - 1u);
        float2 offset = (frac(place) - 0.5) * float2(inputs.size) / float2(pictureSize);
        float weight = exp(-SampleFalloff * dot(offset, offset));

        // The range of the 3 by 3 texels' colours and their sum, the pixel as this frame sees it, each texel weighted by
        // how near its sample fell, in its own texels, so an upscaled pixel is smooth rather than a texel's block, and
        // the nearest of them, whose velocity the pixel takes so an edge moves with what is in front of it.
        float3 sum = float3(0.0);
        float3 weighted = float3(0.0);
        float weights = 0.0;
        float3 low = float3(HalfMax);
        float3 high = float3(-HalfMax);
        float nearestDepth = 0.0;
        uint2 nearest = texel;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                uint2 neighbour = uint2(clamp(int2(texel) + int2(x, y), int2(0), int2(pictureSize) - 1));
                float3 color = inputs.picture.Load(neighbour, 0u).rgb;
                float2 apart = float2(neighbour) + 0.5 - place;
                float spread = exp(-SampleFalloff * dot(apart, apart));
                weighted += color * spread;
                weights += spread;
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

        float3 current = weighted / weights;
        float viewDepth = min(inputs.viewDepth, HalfMax);
        if (!inputs.hasHistory)
            return float4(current, viewDepth);

        float2 velocity = inputs.velocity.Load(nearest, 0u).xy;
        float2 historyUv = inputs.uv + velocity;
        bool isOnScreen = all(saturate(historyUv) == historyUv);
        historyUv = saturate(historyUv);

        // Where the surface moved and the history saw another one where it was, the pixel as it is now.
        float historyDepth = inputs.history.Load(min(uint2(historyUv * float2(inputs.size)), inputs.size - 1u), 0u).a;
        float previousDepth = inputs.velocity.Load(texel, 0u).z;
        if (length(velocity) > MovingDistance && abs(previousDepth - historyDepth) > SurfaceGapMetres)
            return float4(current, viewDepth);

        // The history read between texels, which blurs, so the more the pixel moved off whole texels the more of now
        // it takes, by the weight of this frame's sample; all of now where its surface was off the screen.
        float3 before = clamp(inputs.history.SampleLevel(inputs.sampler, historyUv, 0.0).rgb, low, high);
        float2 moved = abs(velocity) * float2(inputs.size);
        float blend = isOnScreen ? lerp(0.05, 0.8, frac(max(moved.x, moved.y)) * 0.5) * weight : 1.0;
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
