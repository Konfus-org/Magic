// The light that reaches the view's surfaces indirectly, as whatever traces it leaves it for a frame: the diffuse light
// and the reflections at a part of the picture's size, and the probe grid around them; and how the picture reads them
// back at its own size, blurred where they are still noisy, which anything traced at a part of the picture's size can
// read its own back with. It is a row of the frame's buffer, C#'s GpuIndirectLight, every id 0 where nothing traced it
// this frame, and the surfaces are lit with the sky.
module "Light/Indirect";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Light.Probes;
import Scene.Shadows;

// Where the indirect light is in the frame's buffer, right after the shadows, and its bytes: C#'s GpuIndirectLight.
public static const uint IndirectLightOffset = ShadowsOffset + ShadowsBytes;
public static const uint IndirectLightBytes = 84u;

public struct IndirectLight
{
    // The diffuse light, with its variance in alpha, and the reflections' light, at the size they were traced at.
    public uint diffuse;
    public uint reflections;
    public ProbeGridConstants grid;
}

// The frame's indirect light; every id 0 where nothing traced it.
public IndirectLight GetIndirectLight()
{
    BufferRef frame = { GetPassHeader().x };
    return frame.Load<IndirectLight>(IndirectLightOffset);
}

public ProbeGridConstants GetProbeGrid() { return GetIndirectLight().grid; }

// The picture's pixel a pixel traced at a size stands for: the one at a point of its footprint, from 0 to 1 across and
// down.
public uint2 GetPicturePixel(FrameConstants frame, uint2 traced, uint2 tracedSize, float2 at)
{
    uint2 size = uint2(frame.width, frame.height);
    return min(uint2((float2(traced) + at) * float2(size) / float2(tracedSize)), size - 1u);
}

// The pixel traced at a size whose footprint holds a pixel of the picture.
public uint2 GetTracedTexel(FrameConstants frame, uint2 pixel, uint2 tracedSize)
{
    float2 uv = GetTexelCenterUv(pixel, uint2(frame.width, frame.height));
    return min(uint2(uv * float2(tracedSize)), tracedSize - 1u);
}

// The roughest surface whose reflections are traced; a rougher one reflects its diffuse light, or the sky giving way
// to its diffuse light where none is traced.
public static const float TracedRoughness = 0.6;

// Where the light's variance is over this, the picture blurs it.
static const float BlurVariance = 0.015;

// How fast a blur's weight falls with a neighbour's distance from the pixel's plane, as a part of its view depth.
static const float PlaneSharpness = 10000.0;

// The least a traced pixel off a pixel's surface weighs, so a pixel with none of its own surface around it still reads
// its neighbours'.
static const float OffSurfaceWeight = 1e-3;

// The traced light at a pixel of the picture: its light and variance, and in coverage how much of the
// weight was traced, from 0 to 1.
struct TracedSample
{
    float3 light;
    float variance;
    float coverage;
}

// The four traced pixels around a pixel of the picture, from the corner, each with its weight, 0 where it wasn't
// traced, and in coverage how much of their bilinear weight was traced, from 0 to 1.
public struct TracedTaps
{
    public int2 corner;
    public uint2 size;
    public float4 weights;
    public float coverage;

    // The traced pixel of tap i, 0 to 3, row by row.
    public uint2 GetTexel(uint i) { return uint2(clamp(corner + int2(i & 1u, i >> 1u), int2(0), int2(size) - 1)); }
}

// How much a pixel of the picture at a depth lies on the surface at a camera-relative point facing a unit normal, seen
// at a view depth: 1 on its plane and facing its way, less the further their planes are apart and their normals turn.
float GetSurfaceWeight(FrameConstants frame, uint2 at, float depth, float3 relative, float3 normal, float viewDepth)
{
    TextureRef normalTexture = { frame.gbufferNormal };
    float3 atNormal = DecodeOctahedral(normalTexture.Load(at, 0u).xy * 2.0 - 1.0);
    float3 gap = relative - ReconstructRelative(frame, GetTexelCenterUv(at, uint2(frame.width, frame.height)), depth);
    float planeError = max(abs(dot(gap, atNormal)), abs(dot(gap, normal))) / viewDepth;
    return exp(-Pow2(planeError) * PlaneSharpness) * Pow4(saturate(dot(atNormal, normal)));
}

// The four pixels traced at a size around a pixel of the picture, whose surface is at a camera-relative point facing a
// unit normal at a view depth, weighed for blending by hand: each by its bilinear weight where its own pixel, the one
// at a point of its footprint from 0 to 1 across and down, was traced, something drawn there no rougher than a
// roughness, times how much it lies on the pixel's surface, so an edge takes in little of what lies off it; and not at
// all where it wasn't traced.
public TracedTaps GetTracedTaps(
    FrameConstants frame,
    uint2 size,
    float2 at,
    uint2 pixel,
    float3 relative,
    float3 normal,
    float viewDepth,
    float roughest)
{
    TextureRef depthTexture = { frame.depth };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float2 texel = GetTexelCenterUv(pixel, uint2(frame.width, frame.height)) * float2(size) - 0.5;
    float2 f = frac(texel);
    float4 bilinear = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
    TracedTaps taps;
    taps.corner = int2(floor(texel));
    taps.size = size;
    taps.weights = float4(0.0);
    taps.coverage = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 traced = GetPicturePixel(frame, taps.GetTexel(i), size, at);
        float depth = depthTexture.Load(traced, 0u).x;
        if (depth == 0.0 || surfaceTexture.Load(traced, 0u).x > roughest)
            continue;

        float onSurface = max(GetSurfaceWeight(frame, traced, depth, relative, normal, viewDepth), OffSurfaceWeight);
        taps.weights[i] = bilinear[i] * onSurface;
        taps.coverage += bilinear[i];
    }

    return taps;
}

// The traced light around a pixel of the picture, blended by the taps' weights.
TracedSample SampleCovered(
    FrameConstants frame, TextureRef light, uint2 pixel, float3 relative, float3 normal, float viewDepth, float roughest)
{
    TracedTaps taps =
        GetTracedTaps(frame, light.GetSize(0u), float2(0.0), pixel, relative, normal, viewDepth, roughest);
    float4 sum = float4(0.0);
    float weightSum = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        if (taps.weights[i] <= 0.0)
            continue;

        sum += light.Load(taps.GetTexel(i), 0u) * taps.weights[i];
        weightSum += taps.weights[i];
    }

    TracedSample sample;
    sample.light = weightSum > 0.0 ? sum.rgb / weightSum : float3(0.0);
    sample.variance = weightSum > 0.0 ? sum.a / weightSum : 0.0;
    sample.coverage = taps.coverage;
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
    uint2 tracedSize = light.GetSize(0u);
    TextureRef depthTexture = { frame.depth };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float viewDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);
    TracedSample center = SampleCovered(frame, light, pixel, relative, normal, viewDepth, roughest);
    if (center.variance <= BlurVariance || radius < 1.0)
        return float4(center.light, center.coverage);

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

            float weight = exp(-Pow2(float(r) / sigma))
                * GetSurfaceWeight(frame, at, depth, relative, normal, viewDepth);
            sum += light.Load(GetTracedTexel(frame, at, tracedSize), 0u).rgb * weight;
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
