// Temporal upscaling: the view's output, the finished picture at the render size, taken to the window's size. Each
// window pixel is made from the picture's 3 by 3 texels around it, each weighted by how near this frame's sample fell
// to the pixel, and blended with what the last frame resolved where its surface was, so the camera's jitter, a
// different point in each texel each frame, adds up to smooth edges and to detail finer than the picture's texels. The
// history is held to the range of the 3 by 3 texels, so light that changed doesn't trail, and left out where the last
// frame saw another surface there or the surface was off the screen. Adapted from Wicked Engine's temporal
// anti-aliasing, with the weights from Karis, "High Quality Temporal Supersampling" (SIGGRAPH 2014).
//
// The pass header's y names the history, last frame's resolved picture, or its z where there is none; z the picture
// this frame resolves, the window's size, linear, with each pixel's view depth in alpha for the next frame's test; w
// the output, the resolved picture encoded for the screen.
module "TaaUpscaling";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;

// How fast a sample's weight falls with its distance from the pixel, in pixels: a Gaussian close to the
// Blackman-Harris window over a pixel, from Karis.
static const float SampleFalloff = 2.29;

// How far in metres the surface the history saw may be from where the pixel's surface was before it is another.
static const float SurfaceGapMetres = 1.0;

// How far a pixel's surface moves across the screen in a frame, in texture coordinates, before the history can be of
// another surface.
static const float MovingDistance = 0.01;

// The most a 16-bit float holds, which the view depth is kept under.
static const float HalfMax = 65504.0;

[shader("compute")]
[numthreads(8, 8, 1)]
void UpscalePicture(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    StorageRef resolved = { header.z, 0u };
    StorageRef output = { header.w, 0u };
    uint2 size = output.GetSize();
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
            float3 color = ToLinear(picture.Load(neighbour, 0u).rgb);
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
        Store(resolved, output, thread.xy, current, viewDepth);
        return;
    }

    TextureRef history = { header.y };
    float2 velocity = velocities.Load(nearest, 0u).xy;
    float2 historyUv = uv + velocity;
    bool isOnScreen = all(saturate(historyUv) == historyUv);
    historyUv = saturate(historyUv);

    // Where the surface moved and the history saw another one where it was, the pixel as it is now.
    float historyDepth = history.Load(min(uint2(historyUv * float2(size)), size - 1u), 0u).a;
    float previousDepth = velocities.Load(texel, 0u).z;
    if (length(velocity) > MovingDistance && abs(previousDepth - historyDepth) > SurfaceGapMetres)
    {
        Store(resolved, output, thread.xy, current, viewDepth);
        return;
    }

    // The history read between pixels, which blurs, so the more the pixel moved off whole pixels the more of now it
    // takes, by the weight of this frame's sample; all of now where its surface was off the screen.
    float3 before = clamp(LoadBilinear(history, historyUv, size).rgb, low, high);
    float2 moved = abs(velocity) * float2(size);
    float blend = isOnScreen ? lerp(0.05, 0.8, frac(max(moved.x, moved.y)) * 0.5) * weight : 1.0;
    Store(resolved, output, thread.xy, lerp(before, current, blend), viewDepth);
}

// The pixel resolved, kept with its view depth for the next frame and shown encoded for the screen.
void Store(StorageRef resolved, StorageRef output, uint2 pixel, float3 color, float viewDepth)
{
    resolved.Store(pixel, float4(color, viewDepth));
    output.Store(pixel, float4(ToSrgb(color), 1.0));
}

// The texture at uv, filtered linearly between its four nearest texels of level 0, the texture's size.
float4 LoadBilinear(TextureRef texture, float2 uv, uint2 size)
{
    float2 place = uv * float2(size) - 0.5;
    int2 first = int2(floor(place));
    float2 along = place - float2(first);
    int2 last = int2(size) - 1;
    float4 top = lerp(
        texture.Load(uint2(clamp(first, int2(0), last)), 0u),
        texture.Load(uint2(clamp(first + int2(1, 0), int2(0), last)), 0u),
        along.x
    );
    float4 bottom = lerp(
        texture.Load(uint2(clamp(first + int2(0, 1), int2(0), last)), 0u),
        texture.Load(uint2(clamp(first + int2(1, 1), int2(0), last)), 0u),
        along.x
    );
    return lerp(top, bottom, along.y);
}
