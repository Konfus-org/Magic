// Temporal upscaling: the view's output, the finished picture at the render size, taken to the size it is shown at.
// Each shown pixel is made from the picture's 3 by 3 texels around it, each weighted by how near this frame's sample
// fell to the pixel, and blended with what the last frame resolved where its surface was, so the camera's jitter, a
// different point in each texel each frame, adds up to smooth edges and to detail finer than the picture's texels. The
// history is held to the range of the 3 by 3 texels, so light that changed doesn't trail, and left out where the last
// frame saw another surface there or the surface was off the screen. Adapted from Wicked Engine's temporal
// anti-aliasing, with the weights from Karis, "High Quality Temporal Supersampling" (SIGGRAPH 2014).
//
// It works in the output's encoding for the screen, so the picture it resolves is the one the window shows, sharpened
// and dithered by SharpenPicture below. The pass header's y names the history, last frame's resolved picture, or its z
// where there is none. Its z names the picture this frame resolves, the size it is shown at, with each pixel's view
// depth in alpha for the next frame's test. Its w names the pass's UpscaleConstants.
module "TaaUpscaling";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Post.TemporalAntiAliasing;

// C#'s UpscaleConstants: the linear clamp sampler the history is read through, and how much of now each pixel takes at
// least where the history is on the screen.
public struct UpscaleConstants
{
    public uint sampler;
    public float leastBlend;
}

// How fast a sample's weight falls with its distance from the pixel, in pixels: a Gaussian close to the
// Blackman-Harris window over a pixel, from Karis.
static const float SampleFalloff = 2.29;

[shader("compute")]
[numthreads(8, 8, 1)]
void UpscalePicture(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    StorageRef resolved = { header.z, 0u };
    uint2 size = resolved.GetSize();
    if (any(thread.xy >= size))
        return;

    FrameConstants frame = GetFrame();
    TextureRef picture = { frame.output };
    TextureRef depths = { frame.depth };
    TextureRef velocities = { frame.velocity };
    uint2 pictureSize = uint2(frame.width, frame.height);
    float2 uv = GetTexelCenterUv(thread.xy, size);

    // The picture's texel whose sample this frame fell nearest the pixel, as texel i sees the scene at
    // i + 0.5 - jitter, and how far from the pixel that was, in the window's pixels.
    float2 place = uv * float2(pictureSize) + frame.jitter;
    uint2 texel = min(uint2(max(place, 0.0)), pictureSize - 1u);
    float2 offset = (frac(place) - 0.5) * float2(size) / float2(pictureSize);
    float weight = exp(-SampleFalloff * dot(offset, offset));

    // The pixel as this frame sees it, the 3 by 3 texels each weighted by how near its sample fell, in the picture's
    // texels, so a pixel is smooth rather than a texel's block. Their range, and the nearest of them, whose velocity
    // the pixel takes so an edge moves with what is in front of it.
    float3 weighted = float3(0.0);
    float weights = 0.0;
    float3 low = float3(1.0);
    float3 high = float3(0.0);
    float nearestDepth = 0.0;
    uint2 nearest = texel;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 neighbour = uint2(clamp(int2(texel) + int2(x, y), int2(0), int2(pictureSize) - 1));
            float3 color = picture.Load(neighbour, 0u).rgb;
            float2 apart = float2(neighbour) + 0.5 - place;
            float spread = exp(-SampleFalloff * dot(apart, apart));
            weighted += color * spread;
            weights += spread;
            low = min(low, color);
            high = max(high, color);
            float depth = depths.Load(neighbour, 0u).x;
            if (depth > nearestDepth)
            {
                nearestDepth = depth;
                nearest = neighbour;
            }
        }
    }

    float3 current = weighted / weights;
    float depth = depths.Load(texel, 0u).x;
    float viewDepth = depth > 0.0 ? min(ToViewDepth(depth, frame.near), HalfMax) : 0.0;
    if (header.y == header.z)
    {
        resolved.Store(thread.xy, float4(current, viewDepth));
        return;
    }

    TextureRef history = { header.y };
    float2 velocity = velocities.Load(nearest, 0u).xy;
    float2 historyUv = uv + velocity;
    bool isOnScreen = IsOnScreen(historyUv);
    historyUv = saturate(historyUv);

    // Where the surface moved and the history saw another one where it was, the pixel as it is now.
    float historyDepth = history.Load(GetTexelAt(historyUv, size), 0u).a;
    float previousDepth = velocities.Load(texel, 0u).z;
    if (length(velocity) > MovingDistance && abs(previousDepth - historyDepth) > SurfaceGapMetres)
    {
        resolved.Store(thread.xy, float4(current, viewDepth));
        return;
    }

    // The history read between pixels, which blurs, so the more the pixel moved off whole pixels the more of now it
    // takes, by the weight of this frame's sample. All of now where its surface was off the screen.
    UpscaleConstants upscale = LoadConstants<UpscaleConstants>(header.w);
    SamplerRef sampler = { upscale.sampler };
    float3 before = clamp(history.SampleLevel(sampler, historyUv, 0.0).rgb, low, high);
    float2 moved = abs(velocity) * float2(size);
    float blend = isOnScreen
        ? max(lerp(0.05, 0.8, frac(max(moved.x, moved.y)) * 0.5) * weight, upscale.leastBlend)
        : 1.0;
    resolved.Store(thread.xy, float4(lerp(before, current, blend), viewDepth));
}

// The most that each of the four neighbours can take off, kept well under a quarter, where the result's divisor
// reaches 0.
static const float MostSharpening = 0.1875;

// Contrast-adaptive sharpening of the resolved picture, written from AMD's description of robust contrast-adaptive
// sharpening in FidelityFX Super Resolution 1. Each pixel takes off its four neighbours at a weight that is as high as
// the 5 texels' range lets it be without leaving 0 to 1, times the sharpness, so strong edges sharpen less than soft
// detail. The result is held to the 5 texels' range, so an edge can't ring. A sharpness of 0 copies the picture. The
// pass header's y names the resolved picture, its z the sharpened picture it writes, and its w the sharpness, 0 to 1,
// as a float's bits.
[shader("compute")]
[numthreads(8, 8, 1)]
void SharpenPicture(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    StorageRef sharpened = { header.z, 0u };
    uint2 size = sharpened.GetSize();
    if (any(thread.xy >= size))
        return;

    TextureRef picture = { header.y };
    int2 last = int2(size) - 1;
    float4 center = picture.Load(thread.xy, 0u);
    float3 up = picture.Load(uint2(max(int2(thread.xy) - int2(0, 1), int2(0))), 0u).rgb;
    float3 left = picture.Load(uint2(max(int2(thread.xy) - int2(1, 0), int2(0))), 0u).rgb;
    float3 right = picture.Load(uint2(min(int2(thread.xy) + int2(1, 0), last)), 0u).rgb;
    float3 down = picture.Load(uint2(min(int2(thread.xy) + int2(0, 1), last)), 0u).rgb;
    float3 low = min(center.rgb, min(min(up, down), min(left, right)));
    float3 high = max(center.rgb, max(max(up, down), max(left, right)));

    // The neighbours' weight, which is negative: the most that keeps the result at or above 0 where the neighbours are
    // at their highest, and at or below 1 where they are at their lowest, in the channel that allows the least.
    float3 toBlack = -low / (4.0 * max(high, 1.0 / 1024.0));
    float3 toWhite = (1.0 - high) / (4.0 * min(low - 1.0, -1.0 / 1024.0));
    float3 limits = max(toBlack, toWhite);
    float weight = clamp(max(limits.r, max(limits.g, limits.b)), -MostSharpening, 0.0) * asfloat(header.w);

    float3 color = (center.rgb + weight * (up + down + left + right)) / (1.0 + 4.0 * weight);

    // Triangular noise of up to one 8-bit step, new each frame, so a smooth gradient has no bands where the window
    // rounds the picture to 8 bits (Lipshitz, Wannamaker and Vanderkooy, "Quantization and Dither: A Theoretical
    // Survey", 1992). The same noise in each channel keeps it grey.
    float2 random = GetRandom(thread.xy, GetFrame().frameIndex, 0u).xy;
    float dither = (random.x + random.y - 1.0) / 255.0;
    sharpened.Store(thread.xy, float4(clamp(color, low, high) + dither, center.a));
}
