// The traced diffuse light: what a white diffuse surface reflects of the light that reaches it indirectly, its
// irradiance over pi, traced at half the picture's size, filtered and blended over frames, and how the picture reads it
// back at its own size. C#'s TracedDiffuseConstants is in the buffer whose heap id is the pass header's w.
module "Scene/TracedDiffuse";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;

// How many of the picture's pixels a traced pixel stands for along each side.
public static const uint TracedScale = 2u;

public struct TracedDiffuseConstants
{
    // The rays' light at each traced pixel.
    public uint traced;
    // The rays' light filtered over the neighbours, with its brightness's variance in alpha.
    public uint filtered;
    // This frame's light blended with the frames before, with its variance in alpha, and the view depth of each of its
    // pixels; and the last frame's of both, which this frame's blends with.
    public uint light;
    public uint depth;
    public uint lightHistory;
    public uint depthHistory;
    // Linear, clamped to the edges.
    public uint sampler;
    // 0 while the history holds nothing of the view: this frame starts it afresh.
    public uint hasHistory;
}

public TracedDiffuseConstants GetTracedDiffuse() { return LoadConstants<TracedDiffuseConstants>(GetPassHeader().w); }

// The traced textures' size.
public uint2 GetTracedSize(FrameConstants frame) { return max(uint2(frame.width, frame.height) / TracedScale, 1u); }

// Where the blended light's variance is over this, the picture blurs it over this many of its pixels each way.
static const float BlurVariance = 0.015;
static const int BlurRadius = 8;

// How fast the blur's weight falls with distance, and with a neighbour's distance from the pixel's plane as a part of
// its view depth.
static const float BlurSigma = 7.2;
static const float PlaneSharpness = 10000.0;

// The diffuse light at a pixel of the picture whose surface is at a camera-relative point facing a unit normal: the
// blended light read between the traced pixels, and where it is still noisy, blurred along the pixel's row and column
// over the pixels on its surface, weighed by how near their planes and normals are to its own.
public float3 SampleTracedDiffuse(
    FrameConstants frame, TracedDiffuseConstants diffuse, uint2 pixel, float3 relative, float3 normal)
{
    uint2 size = uint2(frame.width, frame.height);
    TextureRef light = { diffuse.light };
    SamplerRef sampler = { diffuse.sampler };
    float4 center = light.SampleLevel(sampler, GetTexelCenterUv(pixel, size), 0.0);
    if (center.a <= BlurVariance)
        return center.rgb;

    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    float viewDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);

    // The pixel itself counts once along each axis.
    float3 sum = center.rgb * 2.0;
    float weightSum = 2.0;
    for (uint axis = 0u; axis < 2u; axis++)
    {
        int2 step = axis == 0u ? int2(1, 0) : int2(0, 1);
        for (int r = -BlurRadius; r <= BlurRadius; r++)
        {
            uint2 at = uint2(int2(pixel) + step * r);
            if (r == 0 || any(at >= size))
                continue;

            float depth = depthTexture.Load(at, 0u).x;
            if (depth == 0.0)
                continue;

            float2 uv = GetTexelCenterUv(at, size);
            float3 atNormal = DecodeOctahedral(normalTexture.Load(at, 0u).xy * 2.0 - 1.0);
            float3 gap = relative - ReconstructRelative(frame, uv, depth);
            float planeError = max(abs(dot(gap, atNormal)), abs(dot(gap, normal))) / viewDepth;
            float weight = exp(-Pow2(float(r) / BlurSigma))
                * exp(-Pow2(planeError) * PlaneSharpness)
                * Pow4(saturate(dot(atNormal, normal)));
            sum += light.SampleLevel(sampler, uv, 0.0).rgb * weight;
            weightSum += weight;
        }
    }

    return sum / weightSum;
}
