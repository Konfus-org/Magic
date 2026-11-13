// What the gem's passes share: the GI buffer. The light a ray brings back from what it lands on, and for the light
// traced at a part of the picture's size, the diffuse and the reflections alike, which pixel of each traced pixel's
// footprint traces this frame, how a pass averages its neighbours' light, and its history found where the surface was
// last frame. C#'s GIConstants is the GI buffer, the pass header's y.
module "Traced";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Light.Probes;
import Light.LocalLights;
import Light.Indirect;
import Sky.ISky;
import Trace.HitSurface;

// What a traced effect blends over frames: this frame's light, with its variance in alpha, and last frame's, which this
// frame's blends with, 0 while it holds nothing of the view and this frame starts it afresh.
public struct TracedBlendConstants
{
    public uint light;
    public uint lightHistory;
    public uint hasHistory;
}

public struct TracedDiffuseConstants
{
    // The rays' light at each traced pixel.
    public uint traced;
    // The rays' light filtered over the neighbours, with its brightness's variance in alpha.
    public uint filtered;
    public TracedBlendConstants blend;
}

public struct TracedReflectionConstants
{
    // The rays' light at each traced pixel, with how far each went in alpha, and its direction with the density it was
    // drawn at.
    public uint traced;
    public uint rays;
    // The rays' light resolved over the neighbours, with its brightness's variance in alpha, and the farthest of their
    // rays that count.
    public uint resolved;
    public uint distance;
    public TracedBlendConstants blend;
}

// What the probe update reads beyond the grid: the frame's rays, the list of which probe traces each, the trace's
// arguments, each probe's count of rays and where they start, and each probe texel's variance, and how far a probe's
// depth reaches, one and a half cells.
public struct ProbeUpdateConstants
{
    public uint rays;
    public uint rayList;
    public uint rayArguments;
    public uint rayCounts;
    public uint variance;
    public float maxDistance;
}

public struct GIConstants
{
    public TracedDiffuseConstants diffuse;
    public TracedReflectionConstants reflections;
    public ProbeUpdateConstants probes;
    // The view depth of each traced pixel, which the diffuse blend writes, and last frame's, which both blends read.
    public uint depth;
    public uint depthHistory;
    // Linear, clamped to the edges: what the history is read with.
    public uint sampler;
    // The traced textures' size.
    public uint2 tracedSize;
}

public GIConstants GetGIConstants() { return LoadConstants<GIConstants>(GetPassHeader().y); }

// The light that reaches a surface from one light picked at random by xi.x, the sun among them, aimed at a point of it
// by xi.yz, over pi and times how many there are to pick from: what a white diffuse surface reflects of them all, on
// average.
public float3 GetDirectLight<S : ISky>(
    Lights lights,
    RaytracingAccelerationStructure world,
    HitSurface surface,
    float3 xi
)
{
    uint lightCount = min(lights.count, MaxLights);
    uint picked = min(uint(xi.x * float(lightCount + 1u)), lightCount);
    float3 origin = surface.position + surface.normal * 0.01;
    float3 direction;
    float3 irradiance;
    float distance;
    if (picked == lightCount)
    {
        direction = SampleSunDirection(lights.sunDirection.xyz, xi.yz);
        irradiance = GetSunIlluminance<S>(lights);
        distance = 1e6;
    }
    else
    {
        GpuLight light = LoadLight(lights.table, picked);
        LightRay ray = CreateLightRay(origin, SampleLightPoint(light, surface.position, xi.yz));
        direction = ray.direction;
        distance = ray.distance;
        irradiance = SampleLight(light, surface.position, surface.normal).irradiance;
    }

    float cosine = dot(surface.normal, direction);
    if (cosine <= 0.0)
        return float3(0.0);

    float3 through = GetTransmittance(world, origin, direction, distance);
    return float(lightCount + 1u) * irradiance * through * cosine * InvPi;
}

// The light a surface a ray landed on sends back along it: what it reflects of one light picked at random by xi and of
// the probes' light around it, and what it gives off.
public float3 GetHitLight<S : ISky>(
    Lights lights,
    ProbeGridConstants grid,
    RaytracingAccelerationStructure world,
    HitSurface surface,
    float3 xi
)
{
    float3 light = GetDirectLight<S>(lights, world, surface, xi)
        + GetProbeIrradiance(grid, surface.position, surface.normal);
    return light * surface.diffuse + surface.emissive;
}

// The picture's pixel a traced pixel traces from: another of its footprint each frame, the same in all of them, so the
// blend over frames sees every one.
public uint2 GetTracedPixel(FrameConstants frame, uint2 traced, uint2 tracedSize)
{
    float2 shift = GetRandom(uint2(0u), frame.frameIndex, 0u).xy;
    return GetPicturePixel(frame, traced, tracedSize, shift);
}

// The traced pixel a pass averages as sample i of count around a traced pixel: a Hammersley set shifted at random per
// pixel and frame, spread over as many traced pixels across.
public uint2 GetNeighbour(uint2 traced, uint i, uint count, float2 shift, float spread)
{
    float2 offset = (frac(GetHammersley(i, count) + shift) - 0.5) * spread;
    return uint2(int2(float2(traced) + offset));
}

// The weighed light of a pass's neighbours, in a curve that keeps a bright neighbour from outweighing the rest, and its
// brightness's mean and squared differences taken as it goes (West, "Updating Mean and Variance Estimates: An Improved
// Method", 1979).
public struct NeighbourLight
{
    public float3 sum;
    public float weightSum;
    public float mean;
    public float squares;
}

// The neighbours' light with one more, of a weight over 0.
public NeighbourLight AddNeighbour(NeighbourLight neighbours, float3 light, float weight)
{
    float3 color = light / (1.0 + GetLuminance(light));
    neighbours.sum += color * weight;
    neighbours.weightSum += weight;
    float luminance = GetLuminance(color);
    float lastMean = neighbours.mean;
    neighbours.mean += weight / neighbours.weightSum * (luminance - lastMean);
    neighbours.squares += weight * (luminance - lastMean) * (luminance - neighbours.mean);
    return neighbours;
}

// The neighbours' light averaged and brought back out of the curve, with its brightness's variance in alpha. Where none
// weighed anything, the pixel's own light with none.
public float4 GetNeighbourAverage(NeighbourLight neighbours, float3 own)
{
    float4 result = float4(own, 0.0);
    if (neighbours.weightSum > 0.0)
    {
        float3 average = neighbours.sum / neighbours.weightSum;
        result = float4(average / max(1.0 - GetLuminance(average), Epsilon), neighbours.squares / neighbours.weightSum);
    }

    return float4(max(result.rgb, 1e-5), result.a);
}

// The mean and deviation of the 3 by 3 traced pixels' light around one.
public struct Neighbourhood
{
    public float3 mean;
    public float3 deviation;
}

public Neighbourhood GetNeighbourhood(TextureRef light, uint2 traced, uint2 size)
{
    float3 sum = float3(0.0);
    float3 squares = float3(0.0);
    for (uint i = 0u; i < 9u; i++)
    {
        uint2 neighbour = uint2(clamp(int2(traced) + int2(i % 3u, i / 3u) - 1, int2(0), int2(size) - 1));
        float3 color = light.Load(neighbour, 0u).rgb;
        sum += color;
        squares += color * color;
    }

    Neighbourhood neighbourhood;
    neighbourhood.mean = sum / 9.0;
    neighbourhood.deviation = sqrt(max(squares / 9.0 - neighbourhood.mean * neighbourhood.mean, 0.0));
    return neighbourhood;
}

// How alike the history's depth must be to the surface's to be the same surface.
public static const float SameSurface = 0.9;

// How alike a history texel's view depth is to the surface's last frame: 1 the same, falling as they part.
float GetSameness(float previousDepth, float historyDepth)
{
    return exp(-abs(historyDepth - previousDepth) / max(previousDepth, Epsilon));
}

float LoadHistoryDepth(TextureRef depthHistory, float2 uv, uint2 size)
{
    return depthHistory.Load(GetTexelAt(saturate(uv), size), 0u).x;
}

// A traced effect's light blended with its history, read at texture coordinates where the surface last frame at a view
// depth was. Where the texel there saw another surface, the nearest of its neighbours that saw this one is read, and
// failing that the four around it blended by hand. The history is held within spread deviations of the neighbours'
// mean, so light that changed doesn't trail, keeping blend of it, and the variance keeping varianceBlend of its own.
// Where the history saw another surface or lies off the screen, the light alone at the most variance, so the picture
// blurs what is new.
public float4 BlendHistory(
    GIConstants gi,
    TracedBlendConstants effect,
    float4 current,
    Neighbourhood neighbourhood,
    float previousDepth,
    float2 uv,
    float blend,
    float spread,
    float varianceBlend
)
{
    TextureRef light = { effect.lightHistory };
    TextureRef depthHistory = { gi.depthHistory };
    SamplerRef sampler = { gi.sampler };
    uint2 size = gi.tracedSize;

    // Written so a depth the history never held, NaN, counts as another surface.
    float sameness = GetSameness(previousDepth, LoadHistoryDepth(depthHistory, uv, size));
    if (!(sameness >= SameSurface))
    {
        float2 nearest = uv;
        for (uint i = 0u; i < 9u; i++)
        {
            float2 at = uv + float2(int2(i % 3u, i / 3u) - 1) / float2(size);
            float candidate = GetSameness(previousDepth, LoadHistoryDepth(depthHistory, at, size));
            if (!(candidate > sameness))
                continue;

            sameness = candidate;
            nearest = at;
        }

        uv = nearest;
    }

    float4 previous;
    if (sameness >= SameSurface)
        previous = light.SampleLevel(sampler, uv, 0.0);
    else
    {
        float2 texel = uv * float2(size) - 0.5;
        float2 f = frac(texel);
        uint2 corner = uint2(max(floor(texel), 0.0));
        float4 weights = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
        previous = float4(0.0);
        float depth = 0.0;
        for (uint i = 0u; i < 4u; i++)
        {
            uint2 at = min(corner + uint2(i & 1u, i >> 1u), size - 1u);
            previous += light.Load(at, 0u) * weights[i];
            depth += depthHistory.Load(at, 0u).x * weights[i];
        }

        sameness = GetSameness(previousDepth, depth);
    }

    if (!(sameness >= SameSurface) || any(saturate(uv) != uv))
        return float4(current.rgb, 1.0);

    float3 range = spread * neighbourhood.deviation;
    float3 held = clamp(previous.rgb, neighbourhood.mean - range, neighbourhood.mean + range);
    return float4(lerp(current.rgb, held, blend), max(lerp(current.a, previous.a, varianceBlend), 0.0));
}
