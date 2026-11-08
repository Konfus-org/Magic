// Temporal anti-aliasing: each pixel's lit colour blended with what the last frame resolved where its surface was, so
// the camera's jitter, a different point in the pixel each frame, adds up to smooth edges over frames. The history is
// held to the range of the pixel's 3 by 3 neighbours, so light that changed doesn't trail, and left out where the last
// frame saw another surface there or the surface was off the screen. Adapted from Wicked Engine's temporal AA. The
// header's y is last frame's resolved picture, z this frame's, which the pass writes, and w the linear sampler the
// history is read through; each resolved texel keeps its view depth in alpha, 0 for the sky, for the next frame's test.
module "ResolveTemporal";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;

// How far in metres the surface the history saw may be from where the pixel's surface was before it is another.
static const float SurfaceGapMetres = 1.0;

// How far a pixel's surface moves across the screen in a frame, in texture coordinates, before the history can be of
// another surface.
static const float MovingDistance = 0.01;

// The most a 16-bit float holds, which the picture is kept under.
static const float HalfMax = 65504.0;

// A colour in Reinhard's curve after the exposure, where the blend is done, so a bright pixel doesn't outweigh the
// neighbours it is averaged with; and back.
float3 ToBlendSpace(float3 color, float exposure)
{
    float3 exposed = color * exposure;
    return exposed / (exposed + 1.0);
}

float3 FromBlendSpace(float3 color, float exposure) { return color / max(1.0 - color, Epsilon) / exposure; }

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveTemporal(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    PassHeader header = GetPassHeader();
    TextureRef picture = { frame.hdr };
    TextureRef depthTexture = { frame.depth };

    // The range of the 3 by 3 neighbours' colours, and the nearest of them, whose velocity the pixel takes so an edge
    // moves with what is in front of it.
    float3 low = float3(HalfMax);
    float3 high = float3(-HalfMax);
    float nearestDepth = 0.0;
    uint2 nearest = thread.xy;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 neighbour = uint2(clamp(int2(thread.xy) + int2(x, y), int2(0), int2(size) - 1));
            float3 color = picture.Load(neighbour, 0u).rgb;
            low = min(low, color);
            high = max(high, color);
            float depth = depthTexture.Load(neighbour, 0u).x;
            if (depth > nearestDepth)
            {
                nearestDepth = depth;
                nearest = neighbour;
            }
        }
    }

    TextureRef velocityTexture = { frame.velocity };
    float2 velocity = velocityTexture.Load(nearest, 0u).xy;
    float2 historyUv = GetTexelCenterUv(thread.xy, size) + velocity;
    bool isOnScreen = all(saturate(historyUv) == historyUv);
    historyUv = saturate(historyUv);

    float3 current = picture.Load(thread.xy, 0u).rgb;
    float depth = depthTexture.Load(thread.xy, 0u).x;
    float viewDepth = depth > 0.0 ? min(ToViewDepth(depth, frame.near), HalfMax) : 0.0;
    StorageRef output = { header.z, 0u };

    // Where the surface moved and the history saw another one where it was, the pixel as it is now.
    TextureRef history = { header.y };
    float historyDepth = history.Load(min(uint2(historyUv * float2(size)), size - 1u), 0u).a;
    float previousDepth = velocityTexture.Load(thread.xy, 0u).z;
    if (length(velocity) > MovingDistance && abs(previousDepth - historyDepth) > SurfaceGapMetres)
    {
        output.Store(thread.xy, float4(current, viewDepth));
        return;
    }

    // The history read between texels, which blurs, so the more the pixel moved off whole texels the more of now it
    // takes; all of now where its surface was off the screen.
    SamplerRef sampler = { header.w };
    float3 before = clamp(history.SampleLevel(sampler, historyUv, 0.0).rgb, low, high);
    float2 moved = abs(velocity) * float2(size);
    float blend = isOnScreen ? lerp(0.05, 0.8, frac(max(moved.x, moved.y)) * 0.5) : 1.0;
    float exposure = GetExposure(frame);
    float3 resolved = FromBlendSpace(
        lerp(ToBlendSpace(before, exposure), ToBlendSpace(current, exposure), blend),
        exposure
    );
    output.Store(thread.xy, float4(min(resolved, HalfMax), viewDepth));
}
