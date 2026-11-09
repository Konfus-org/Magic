// The light that reaches the view's surfaces indirectly, as whatever traces it leaves it for a frame: the diffuse light
// and the reflections at half the picture's size, and the probe grid around them; and how the picture reads them back
// at its own size, blurred where they are still noisy. A frame none was written for reads none, and lights its surfaces
// with the sky. C#'s IndirectLight is the GPU provider's buffer the frame names.
module "Light/Indirect";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Light.Probes;

public struct IndirectLight
{
    // The frame these are for, counted from 1, so a buffer never written is for none.
    public uint frameNumber;
    // The diffuse light, with its variance in alpha, and the reflections' light, at half the picture's size.
    public uint diffuse;
    public uint reflections;
    public uint padding0;
    public ProbeGridConstants grid;
}

// Whether the indirect light in a buffer was written for the frame: not where the frame's buffer holds another frame,
// or nothing yet.
public bool IsWrittenFor(uint indirectLight, FrameConstants frame)
{
    BufferRef buffer = { indirectLight };
    return buffer.Load<uint>(0u) == frame.frameIndex + 1u;
}

// The indirect light written for this frame; every id 0 where none was.
public IndirectLight GetIndirectLight(FrameConstants frame)
{
    BufferRef buffer = { frame.indirectLight };
    IndirectLight indirect = buffer.Load<IndirectLight>(0u);
    if (IsWrittenFor(frame.indirectLight, frame))
        return indirect;

    IndirectLight none = {};
    return none;
}

public ProbeGridConstants GetProbeGrid() { return GetIndirectLight(GetFrame()).grid; }

// How many of the picture's pixels a traced pixel stands for along each side.
public static const uint TracedScale = 2u;

// The traced textures' size.
public uint2 GetTracedSize(FrameConstants frame) { return max(uint2(frame.width, frame.height) / TracedScale, 1u); }

// The roughest surface whose reflections are traced; a rougher one reflects its diffuse light, or the sky giving way
// to its diffuse light where none is traced.
public static const float TracedRoughness = 0.6;

// Where the light's variance is over this, the picture blurs it.
static const float BlurVariance = 0.015;

// How fast a blur's weight falls with a neighbour's distance from the pixel's plane, as a part of its view depth.
static const float PlaneSharpness = 10000.0;

// The traced light at texture coordinates of the picture: its light and variance, and in coverage how much of the
// weight was traced, from 0 to 1.
struct TracedSample
{
    float3 light;
    float variance;
    float coverage;
}

// The four traced pixels around texture coordinates blended by hand, each by its bilinear weight where its own pixel
// was traced, something drawn there no rougher than a roughness, and not at all where it wasn't, so a surface's edge
// takes in none of what lies off it.
TracedSample SampleCovered(FrameConstants frame, TextureRef light, float2 uv, float roughest)
{
    TextureRef depthTexture = { frame.depth };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    uint2 size = GetTracedSize(frame);
    float2 texel = uv * float2(size) - 0.5;
    float2 f = frac(texel);
    int2 corner = int2(floor(texel));
    float4 weights = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
    float4 sum = float4(0.0);
    float coverage = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 at = uint2(clamp(corner + int2(i & 1u, i >> 1u), int2(0), int2(size) - 1));
        uint2 pixel = at * TracedScale;
        bool isTraced = depthTexture.Load(pixel, 0u).x > 0.0 && surfaceTexture.Load(pixel, 0u).x <= roughest;
        float weight = isTraced ? weights[i] : 0.0;
        sum += light.Load(at, 0u) * weight;
        coverage += weight;
    }

    TracedSample sample;
    sample.light = coverage > 0.0 ? sum.rgb / coverage : float3(0.0);
    sample.variance = coverage > 0.0 ? sum.a / coverage : 0.0;
    sample.coverage = coverage;
    return sample;
}

// The traced light at a pixel of the picture whose surface is at a camera-relative point facing a unit normal, in rgb,
// and how much of it was traced, from 0 to 1, in alpha: read between the traced pixels no rougher than a roughness, and
// where it is still noisy, blurred along the pixel's row and column over up to a radius of pixels on its surface no
// rougher than that, each read from its own traced pixel, weighed by distance and by how near their planes and normals
// are to its own.
float4 SampleTraced(
    FrameConstants frame, TextureRef light, uint2 pixel, float3 relative, float3 normal, float radius, float roughest)
{
    uint2 size = uint2(frame.width, frame.height);
    TracedSample center = SampleCovered(frame, light, GetTexelCenterUv(pixel, size), roughest);
    if (center.variance <= BlurVariance || radius < 1.0)
        return float4(center.light, center.coverage);

    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float viewDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);
    float sigma = radius * 0.9;

    // The pixel itself counts once along each axis.
    float3 sum = center.light * 2.0;
    float weightSum = 2.0;
    for (uint axis = 0u; axis < 2u; axis++)
    {
        int2 step = axis == 0u ? int2(1, 0) : int2(0, 1);
        for (int r = -int(radius); r <= int(radius); r++)
        {
            uint2 at = uint2(int2(pixel) + step * r);
            if (r == 0 || any(at >= size))
                continue;

            float depth = depthTexture.Load(at, 0u).x;
            if (depth == 0.0 || surfaceTexture.Load(at, 0u).x > roughest)
                continue;

            float2 uv = GetTexelCenterUv(at, size);
            float3 atNormal = DecodeOctahedral(normalTexture.Load(at, 0u).xy * 2.0 - 1.0);
            float3 gap = relative - ReconstructRelative(frame, uv, depth);
            float planeError = max(abs(dot(gap, atNormal)), abs(dot(gap, normal))) / viewDepth;
            float weight = exp(-Pow2(float(r) / sigma))
                * exp(-Pow2(planeError) * PlaneSharpness)
                * Pow4(saturate(dot(atNormal, normal)));
            sum += light.Load(at / TracedScale, 0u).rgb * weight;
            weightSum += weight;
        }
    }

    return float4(sum / weightSum, center.coverage);
}

// The diffuse light at a pixel of the picture whose surface is at a camera-relative point facing a unit normal, read
// back from the blended light and blurred over up to 8 pixels where it is still noisy.
public float3 SampleIndirectDiffuse(FrameConstants frame, uint light, uint2 pixel, float3 relative, float3 normal)
{
    TextureRef texture = { light };
    return SampleTraced(frame, texture, pixel, relative, normal, 8.0, 1.0).rgb;
}

// The light reflected towards the view at a pixel of the picture whose surface is at a camera-relative point facing a
// unit normal with a roughness no more than TracedRoughness, from the blended light, in rgb, and how much of it was
// traced in alpha: blurred over up to 2 pixels as the surface roughens towards an eighth.
public float4 SampleIndirectReflections(
    FrameConstants frame, uint light, uint2 pixel, float3 relative, float3 normal, float roughness)
{
    TextureRef texture = { light };
    float radius = 2.0 * saturate(roughness * 8.0);
    return SampleTraced(frame, texture, pixel, relative, normal, radius, TracedRoughness);
}
