// What the gem's passes share: the GI buffer. The light a ray brings back from what it lands on, and for the light
// traced at a part of the picture's size, the diffuse and the reflections alike, which pixel of each traced pixel's
// footprint traces this frame, how a pass averages its neighbours' light, and its history found where the surface was
// last frame. C#'s GIConstants is the GI buffer, the pass header's y. The reflections' gem imports it too.
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
    // The rays' light at each traced pixel, with the share of them that reach the sky in alpha.
    public uint traced;
    // NRD's inputs: the light and hit distance, the motion, the normal and roughness, and the view depth.
    public uint radiance;
    public uint motion;
    public uint normalRoughness;
    public uint viewZ;
    // 1 where the sky share's history holds last frame's.
    public uint hasHistory;
}

// What the probe update reads beyond the grid: the frame's rays, the list of which probe traces each, the trace's
// arguments, each probe's count of rays and where they start, and each probe texel's variance, and how far a probe's
// depth reaches, one and a half cells of the finest cascade.
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
    public ProbeUpdateConstants probes;
    // The view depth of each traced pixel in x, the share of the sky it sees in y and the frames that share blended in
    // z, which the sky share's blend writes, and last frame's, which it reads.
    public uint depth;
    public uint depthHistory;
    // The traced textures' size.
    public uint2 tracedSize;
    // The diffuse light's rays a traced pixel and how far they reach, the most of its history the sky share keeps a
    // frame, and how much of its rays' light a probe takes.
    public uint diffuseRayCount;
    public float diffuseRayRange;
    public float diffuseHistory;
    public float probeBlend;
    // The diffuse rays' terms turned off to measure them: 1, the probes' light at a hit; 2, the sky. PROTO.
    public uint diffuseTermsOff;
}

public GIConstants GetGIConstants() { return LoadConstants<GIConstants>(GetPassHeader().y); }

// The light grid: for each cell, a bit per light whose reach touches the cell, in words as a screen tile keeps them.
// It covers the finest probe cascade, its cells LightCellsPerProbe to a probe cell along each axis, so a cell holds
// fewer lights that do not reach its points. The cells at the grid's sides reach on without end, so every point has a
// cell that holds each light that reaches it. PROTO: the grid's words follow the probes in the probe table, so the
// reflections find it through the probe grid that they already read.

public static const uint LightCellsPerProbe = 2u;

// The light grid's cells along each axis.
public uint3 GetLightGridSize(ProbeGridConstants grid)
{
    return grid.dimensions * LightCellsPerProbe;
}

// The metres along each side of a light grid cell.
public float3 GetLightCellSize(ProbeGridConstants grid)
{
    return grid.cellSize / float(LightCellsPerProbe);
}

// The world cell of the light grid's first cell.
public int3 GetFirstLightCell(ProbeGridConstants grid)
{
    return GetFirstProbeCell(grid, GetFrame().cameraPosition.xyz) * int(LightCellsPerProbe);
}

// The cell of the light grid that holds a point.
public int3 GetLightCell(ProbeGridConstants grid, float3 position)
{
    int3 cell = int3(floor(position / GetLightCellSize(grid))) - GetFirstLightCell(grid);
    return clamp(cell, int3(0), int3(GetLightGridSize(grid)) - 1);
}

// The first of a cell's words in the probe table.
public uint GetLightCellOffset(ProbeGridConstants grid, int3 cell)
{
    uint3 size = grid.dimensions;
    uint probeWords = size.x * size.y * size.z * grid.cascades * (sizeof(GpuProbe) / 4u);
    uint3 cells = GetLightGridSize(grid);
    return probeWords + (cell.x + (cell.y + cell.z * cells.y) * cells.x) * LightBuckets;
}

// The light that reaches a surface from the sun, aimed at a point of its disc by xi.yz, and from one light whose reach
// holds it, picked by xi.x as likely as its unshadowed light there is bright, shadowed by a ray aimed at a point of it
// by xi.yz and divided by how likely the pick was, is: over pi, what a white diffuse surface reflects of them. The
// light grid's cell gives the lights to look at.
public float3 GetDirectLight<S : ISky>(
    Lights lights,
    ProbeGridConstants grid,
    RaytracingAccelerationStructure world,
    HitSurface surface,
    float3 xi
)
{
    float3 origin = surface.position + surface.normal * 0.01;
    float3 light = float3(0.0);
    float3 sunIlluminance = GetSunIlluminance<S>(lights);
    float3 toSun = SampleSunDirection(lights.sunDirection.xyz, xi.yz);
    float sunCosine = dot(surface.normal, toSun);
    if (sunCosine > 0.0 && any(sunIlluminance > 0.0))
        light = sunIlluminance * GetTransmittance(world, origin, toSun, 1e6) * sunCosine;

    uint lightCount = min(lights.count, MaxLights);
    if (lightCount == 0u)
        return light * InvPi;

    // A reservoir of one: each light replaces the pick as likely as its share of the brightness seen so far.
    uint seed = uint(xi.x * 16777216.0);
    uint picked = 0u;
    float targetSum = 0.0;
    float3 pickedLight = float3(0.0);
    float pickedTarget = 0.0;
    ArrayRef<uint> cells = { grid.probes };
    uint offset = GetLightCellOffset(grid, GetLightCell(grid, surface.position));
    uint buckets = (lightCount + 31u) / 32u;
    for (uint bucket = 0u; bucket < buckets; bucket++)
    {
        uint bits = cells[offset + bucket];
        while (bits != 0u)
        {
            uint candidate = bucket * 32u + firstbitlow(bits);
            bits &= bits - 1u;
            GpuLight each = LoadLight(lights.table, candidate);
            if (length(surface.position - each.position) > GetLightReach(each))
                continue;

            LightSample sample = SampleLight(each, surface.position, surface.normal);
            float3 received = sample.irradiance * saturate(dot(surface.normal, sample.direction));
            float target = GetLuminance(received);
            if (target <= 0.0)
                continue;

            targetSum += target;
            seed = GetHash(seed);
            if (ToUnitFloat(seed) * targetSum < target)
            {
                picked = candidate;
                pickedLight = received;
                pickedTarget = target;
            }
        }
    }

    if (targetSum <= 0.0)
        return light * InvPi;

    LightRay ray = CreateLightRay(origin, SampleLightPoint(LoadLight(lights.table, picked), surface.position, xi.yz));
    // Picked as likely as pickedTarget / targetSum.
    light += pickedLight * (targetSum / pickedTarget) * GetTransmittance(world, origin, ray.direction, ray.distance);
    return light * InvPi;
}

// The light a surface a ray landed on sends back along it: what it reflects of the lights, as GetDirectLight picks
// them by xi, and of the probes' light around it, and what it gives off.
public float3 GetHitLight<S : ISky>(
    Lights lights,
    ProbeGridConstants grid,
    RaytracingAccelerationStructure world,
    HitSurface surface,
    float3 xi
)
{
    float3 light = GetDirectLight<S>(lights, grid, world, surface, xi)
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
// Method", 1979). The curve halves a light as bright as level, which the pass takes from what the view does not
// change, so it bends the same at noon and at night and wherever the camera looks.
public struct NeighbourLight
{
    public float3 sum;
    public float weightSum;
    public float mean;
    public float squares;
    public float level;
}

public NeighbourLight CreateNeighbourLight(float level)
{
    NeighbourLight neighbours = {};
    neighbours.level = max(level, Epsilon);
    return neighbours;
}

// The neighbours' light with one more, of a weight over 0.
public NeighbourLight AddNeighbour(NeighbourLight neighbours, float3 light, float weight)
{
    float3 color = light / (neighbours.level + GetLuminance(light));
    neighbours.sum += color * weight;
    neighbours.weightSum += weight;
    float luminance = GetLuminance(color);
    float lastMean = neighbours.mean;
    neighbours.mean += weight / neighbours.weightSum * (luminance - lastMean);
    neighbours.squares += weight * (luminance - lastMean) * (luminance - neighbours.mean);
    return neighbours;
}

// The neighbours' light averaged and brought back out of the curve, with its brightness's variance in the curve in
// alpha. Where none weighed anything, the pixel's own light with none.
public float4 GetNeighbourAverage(NeighbourLight neighbours, float3 own)
{
    float4 result = float4(own, 0.0);
    if (neighbours.weightSum > 0.0)
    {
        float3 average = neighbours.sum / neighbours.weightSum;
        result = float4(
            average * neighbours.level / max(1.0 - GetLuminance(average), 0.001),
            neighbours.squares / neighbours.weightSum
        );
    }

    return float4(max(result.rgb, 1e-5), result.a);
}

// The mean and deviation of the 5 by 5 traced pixels' light around one: a box wide enough that one frame's noise
// hardly moves it.
public struct Neighbourhood
{
    public float3 mean;
    public float3 deviation;
}

public Neighbourhood GetNeighbourhood(TextureRef light, uint2 traced, uint2 size)
{
    float3 sum = float3(0.0);
    float3 squares = float3(0.0);
    for (uint i = 0u; i < 25u; i++)
    {
        uint2 neighbour = uint2(clamp(int2(traced) + int2(i % 5u, i / 5u) - 2, int2(0), int2(size) - 1));
        float3 color = light.Load(neighbour, 0u).rgb;
        sum += color;
        squares += color * color;
    }

    Neighbourhood neighbourhood;
    neighbourhood.mean = sum / 25.0;
    neighbourhood.deviation = sqrt(max(squares / 25.0 - neighbourhood.mean * neighbourhood.mean, 0.0));
    return neighbourhood;
}

// How many times brighter or darker than its neighbours' mean a history may stay: within it, the noise of a few rays,
// most of them dark, is kept; past it, as when a light goes out, the history drops to it at once and does not fade
// over hundreds of frames. PROTO: the 8 is a guess.
static const float HistoryRatio = 8.0;

// How alike the history's depth must be to the surface's to be the same surface.
public static const float SameSurface = 0.98;

// How alike a history texel's view depth is to the surface's last frame: 1 the same, falling as they part.
public float GetSameness(float previousDepth, float historyDepth)
{
    return exp(-abs(historyDepth - previousDepth) / max(previousDepth, Epsilon));
}

float LoadHistoryDepth(TextureRef depthHistory, float2 uv, uint2 size)
{
    return depthHistory.Load(GetTexelAt(saturate(uv), size), 0u).x;
}

// A traced effect's light blended with its history, read with a sampler at texture coordinates where the surface last
// frame at a view depth was, in traced textures of a size whose depth history holds last frame's view depths. Where
// the texel there saw another surface, the nearest of its neighbours that saw this one is read, and failing that the
// four around it blended by hand. The history is held within spread deviations of the neighbours' mean, and within
// HistoryRatio of its brightness, so light that changed doesn't trail, keeping blend of it, and the variance keeping
// varianceBlend of its own.
// Where the history saw another surface or lies off the screen, the light alone at the most variance, so the picture
// blurs what is new.
public float4 BlendHistory(
    uint depthHistoryTexture,
    uint historySampler,
    uint2 size,
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
    TextureRef depthHistory = { depthHistoryTexture };
    SamplerRef sampler = { historySampler };

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
    float heldBrightness = GetLuminance(held);
    float meanBrightness = GetLuminance(neighbourhood.mean);
    if (heldBrightness > 0.0)
        held *= clamp(heldBrightness, meanBrightness / HistoryRatio, meanBrightness * HistoryRatio) / heldBrightness;
    return float4(lerp(current.rgb, held, blend), max(lerp(current.a, previous.a, varianceBlend), 0.0));
}
