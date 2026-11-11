// Temporal upscaling: the view's output, the finished picture at the render size, taken to the size it is shown at.
// Each shown pixel is made from the picture's 3 by 3 texels around it, each weighted by how near this frame's sample fell
// to the pixel, and blended with what the last frame resolved where its surface was, so the camera's jitter, a
// different point in each texel each frame, adds up to smooth edges and to detail finer than the picture's texels. The
// history is held to the range of the 3 by 3 texels, so light that changed doesn't trail, and left out where the last
// frame saw another surface there or the surface was off the screen. Adapted from Wicked Engine's temporal
// anti-aliasing, with the weights from Karis, "High Quality Temporal Supersampling" (SIGGRAPH 2014).
//
// It works in the output's encoding for the screen, so the picture it resolves is the one the window shows. The pass
// header's y names the history, last frame's resolved picture, or its z where there is none; z the picture this frame
// resolves, the size it is shown at, with each pixel's view depth in alpha for the next frame's test; w the linear clamp
// sampler the history is read through.
module "TaaUpscaling";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Post.TemporalAntiAliasing;

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
    // texels, so a pixel is smooth rather than a texel's block; their range; and the nearest of them, whose velocity
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
    // takes, by the weight of this frame's sample; all of now where its surface was off the screen.
    SamplerRef sampler = { header.w };
    float3 before = clamp(history.SampleLevel(sampler, historyUv, 0.0).rgb, low, high);
    float2 moved = abs(velocity) * float2(size);
    float blend = isOnScreen ? lerp(0.05, 0.8, frac(max(moved.x, moved.y)) * 0.5) * weight : 1.0;
    resolved.Store(thread.xy, float4(lerp(before, current, blend), viewDepth));
}
