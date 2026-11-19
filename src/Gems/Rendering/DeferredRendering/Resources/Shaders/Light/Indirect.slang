// The light that reaches the view's surfaces indirectly, as whatever traces it leaves it for a frame: the diffuse light
// and the reflections at a part of the picture's size, and the probe grid around them, and how the picture reads them
// back at its own size, blurred where they are still noisy, which anything traced at a part of the picture's size can
// read its own back with, and how much of it the depth's nearby surfaces hide. It is a row of the frame's buffer, C#'s
// GpuIndirectLight, every id 0 where nothing traced it this frame, and the surfaces are lit with the sky.
module "Light/Indirect";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Light.Probes;
import Scene.Shadows;

// Where the indirect light is in the frame's buffer, right after the shadows, and its bytes: C#'s GpuIndirectLight.
public static const uint IndirectLightOffset = ShadowsOffset + ShadowsBytes;
public static const uint IndirectLightBytes = 56u;

// What the diffuse light is multiplied by before NRD denoises it, and divided by where shading reads it back: NRD keeps
// the light and its square in half floats, which hold up to 65504, so its input must stay below about 250. The light
// is in physical units, a sunlit surface's in the tens of thousands. PROTO: a constant, not the frame's exposure,
// which the post effects meter later in the frame.
public static const float NrdRadianceScale = 1.0 / 128.0;

public struct IndirectLight
{
    // The denoised diffuse light, with its rays' mean hit distance in alpha, and the reflections' light, with its
    // variance in alpha, at the size they were traced at.
    public uint diffuse;
    public uint reflections;
    // In y, the share of the sky each pixel of the diffuse light sees, at its size.
    public uint skyShare;
    public ProbeGridConstants grid;
    // The terms turned off to measure them: 4, the shadow fill; 8, the probes' light where the traced light does not
    // cover the pixel. PROTO.
    public uint termsOff;
}

// The frame's indirect light, every id 0 where nothing traced it.
public IndirectLight GetIndirectLight() { return LoadFrameRow<IndirectLight>(IndirectLightOffset); }

public ProbeGridConstants GetProbeGrid() { return GetIndirectLight().grid; }

// The picture's pixel a pixel traced at a size stands for: the one at a point of its footprint, from 0 to 1 across and
// down. In whole numbers: a quotient of floats can fall just short of a whole pixel and name the one before it.
public uint2 GetPicturePixel(FrameConstants frame, uint2 traced, uint2 tracedSize, float2 at)
{
    uint2 size = uint2(frame.width, frame.height);
    return min((traced * size + uint2(at * float2(size))) / tracedSize, size - 1u);
}

// The pixel traced at a size whose footprint holds a pixel of the picture.
public uint2 GetTracedTexel(FrameConstants frame, uint2 pixel, uint2 tracedSize)
{
    return GetTexelAt(GetTexelCenterUv(pixel, uint2(frame.width, frame.height)), tracedSize);
}

// The roughest surface whose reflections are traced. A rougher one reflects its diffuse light, or the sky giving way
// to its diffuse light where none is traced. A surface gives up its traced reflections for that light over the last
// TracedRoughnessFade of roughness under it, so no line shows where a surface roughens past it.
public static const float TracedRoughness = 0.3;
public static const float TracedRoughnessFade = 0.1;

// Where the light's variance is over this, the picture blurs it.
static const float BlurVariance = 0.015;

// How fast a blur's weight falls with a neighbour's distance from the pixel's plane, as a part of its view depth.
static const float PlaneSharpness = 10000.0;

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
// at a view depth, as its logarithm: 0 on its plane and facing its way, less the further their planes are apart and
// their normals turn.
float GetSurfaceScore(FrameConstants frame, uint2 at, float depth, float3 relative, float3 normal, float viewDepth)
{
    float3 atNormal = LoadNormal(frame.gbufferNormal, at);
    float3 gap = relative - ReconstructPixel(frame, at, depth);
    float planeError = max(abs(dot(gap, atNormal)), abs(dot(gap, normal))) / viewDepth;
    return -Pow2(planeError) * PlaneSharpness + 4.0 * log(max(saturate(dot(atNormal, normal)), 1e-6));
}

// The same as a weight: 1 on its plane and facing its way.
float GetSurfaceWeight(FrameConstants frame, uint2 at, float depth, float3 relative, float3 normal, float viewDepth)
{
    return exp(GetSurfaceScore(frame, at, depth, relative, normal, viewDepth));
}

// The four pixels traced at a size around a pixel of the picture, whose surface is at a camera-relative point facing a
// unit normal at a view depth, weighed for blending by hand: each by its bilinear weight where its own pixel, the one
// at a point of its footprint from 0 to 1 across and down, was traced, something drawn there no rougher than a
// roughness, times how much it lies on the pixel's surface, as a part of the most any tap does, so an edge takes in
// little of what lies off it, a pixel with none of its own surface around reads the tap nearest to it rather than a
// mean of all, and none where it wasn't traced. Its coverage is the share of the taps on its surface that were traced,
// so a tap on another surface behind a silhouette, traced or not, does not lower it. It also falls as far as the tap
// that lies most on its surface lies off it.
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
    float4 scores = float4(-1e30);
    float best = -1e30;
    float bestSurface = -1e30;
    float onSurface = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 traced = GetPicturePixel(frame, taps.GetTexel(i), size, at);
        float depth = depthTexture.Load(traced, 0u).x;
        if (depth == 0.0 || bilinear[i] <= 0.0)
            continue;

        float surface = GetSurfaceScore(frame, traced, depth, relative, normal, viewDepth);
        float share = bilinear[i] * exp(surface);
        onSurface += share;
        if (surfaceTexture.Load(traced, 0u).x > roughest)
            continue;

        scores[i] = surface + log(bilinear[i]);
        best = max(best, scores[i]);
        bestSurface = max(bestSurface, surface);
        taps.coverage += share;
    }

    // A pixel that no tap lies on, such as a thin side seen edge-on, counts as not traced, so it takes the fallback's
    // light rather than another surface's.
    taps.coverage = onSurface > 0.0 ? taps.coverage / onSurface * exp(bestSurface) : 0.0;

    for (uint i = 0u; i < 4u; i++)
    {
        if (scores[i] > -1e29)
            taps.weights[i] = exp(scores[i] - best);
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

            // Weighed by the pixel its traced pixel traced from, which may lie on another surface than its own.
            uint2 texel = GetTracedTexel(frame, at, tracedSize);
            uint2 traced = GetPicturePixel(frame, texel, tracedSize, float2(0.0));
            float depth = depthTexture.Load(traced, 0u).x;
            if (depth == 0.0 || surfaceTexture.Load(traced, 0u).x > roughest)
                continue;

            float weight = exp(-Pow2(float(r) / sigma))
                * GetSurfaceWeight(frame, traced, depth, relative, normal, viewDepth);
            sum += light.Load(texel, 0u).rgb * weight;
            weightSum += weight;
        }
    }

    return float4(sum / weightSum, center.coverage);
}

// The diffuse light at a pixel of the picture whose surface is at a camera-relative point facing a unit normal, read
// between the denoised light's pixels that lie on its surface, in rgb, and how much of it was traced in alpha. Its
// alpha is a hit distance, not a variance, so it takes no blur.
public float4 SampleIndirectDiffuse(FrameConstants frame, uint light, uint2 pixel, float3 relative, float3 normal)
{
    TextureRef texture = { light };
    float4 sample = SampleTraced(frame, texture, pixel, relative, normal, 0.0, 1.0);
    return float4(sample.rgb / NrdRadianceScale, sample.a);
}

// The share of the sky that a pixel of the picture sees, from 0 in a closed room to 1 in the open, whose surface is at
// a camera-relative point facing a unit normal: read from the y of a texture between the traced pixels that lie on its
// surface, or from the one that holds it where none does.
public float SampleSkyShare(FrameConstants frame, uint skyShare, uint2 pixel, float3 relative, float3 normal)
{
    TextureRef texture = { skyShare };
    TextureRef depthTexture = { frame.depth };
    uint2 size = texture.GetSize(0u);
    float viewDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);
    TracedTaps taps = GetTracedTaps(frame, size, float2(0.0), pixel, relative, normal, viewDepth, 1.0);
    float sum = 0.0;
    float weightSum = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        if (taps.weights[i] <= 0.0)
            continue;

        sum += texture.Load(taps.GetTexel(i), 0u).y * taps.weights[i];
        weightSum += taps.weights[i];
    }

    return weightSum > 0.0 ? sum / weightSum : texture.Load(GetTracedTexel(frame, pixel, size), 0u).y;
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

// How far in metres the occlusion of the indirect light looks around a surface, and the most pixels it reaches.
static const float OcclusionRadius = 0.25;
static const float OcclusionMaxPixels = 48.0;

// The slices across the picture the occlusion looks along, and its taps on each side of a slice.
static const uint OcclusionSlices = 2u;
static const uint OcclusionTaps = 3u;

// How high a neighbour must rise above a surface's plane, as the sine of its angle, before it hides any light, so the
// depth's error and a normal map don't darken a flat surface.
static const float OcclusionBias = 0.1;

// How much of the indirect light reaches a pixel of the picture whose surface is at a camera-relative point facing a
// unit normal, from 0 to 1. Along a few slices across the picture, turned at random each frame for the TAA to blend,
// the highest neighbour in the depth on each side of a slice hides 1 - cos of its rise of the surface's
// cosine-weighted hemisphere, less as it lies further off, and none past OcclusionRadius, so a far surface behind an
// object is not darkened around it (Jimenez et al., "Practical Real-Time Strategies for Accurate Indirect Occlusion",
// 2016).
public float GetIndirectOcclusion(FrameConstants frame, uint2 pixel, float3 relative, float3 normal)
{
    float pixels =
        OcclusionRadius * frame.projection[1][1] * 0.5 * float(frame.height) / max(length(relative), Epsilon);
    if (pixels < 1.0)
        return 1.0;

    TextureRef depthTexture = { frame.depth };
    int2 size = int2(frame.width, frame.height);
    float3 xi = GetRandom(pixel, frame.frameIndex, 0u);
    float hidden = 0.0;
    for (uint slice = 0u; slice < OcclusionSlices; slice++)
    {
        float angle = (float(slice) + xi.x) * Pi / float(OcclusionSlices);
        float2 direction = float2(cos(angle), sin(angle));
        for (uint side = 0u; side < 2u; side++)
        {
            float rise = 0.0;
            for (uint tap = 0u; tap < OcclusionTaps; tap++)
            {
                float reach = max((float(tap) + xi.y) / float(OcclusionTaps) * min(pixels, OcclusionMaxPixels), 1.0);
                int2 at = int2(pixel) + int2(round(direction * reach));
                if (any(at < int2(0)) || any(at >= size))
                    break;

                float depth = depthTexture.Load(uint2(at), 0u).x;
                if (depth == 0.0)
                    continue;

                float3 gap = ReconstructPixel(frame, uint2(at), depth) - relative;
                float apart = max(length(gap), Epsilon);
                float sine = saturate((dot(gap, normal) / apart - OcclusionBias) / (1.0 - OcclusionBias));
                rise = max(rise, sine * saturate(1.0 - Pow2(apart / OcclusionRadius)));
            }

            hidden += 1.0 - sqrt(1.0 - rise * rise);
            direction = -direction;
        }
    }

    return 1.0 - hidden / float(2u * OcclusionSlices);
}
