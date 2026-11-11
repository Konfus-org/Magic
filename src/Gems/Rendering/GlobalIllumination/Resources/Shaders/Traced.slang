// What the light traced at a part of the picture's size shares, the diffuse and the reflections alike: the GI buffer,
// which pixel of each traced pixel's footprint traces this frame, how a pass averages its neighbours' light, and its
// history found where the surface was last frame. C#'s GIConstants is the GI buffer, the pass header's y.
module "Traced";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Random;
import Core.Sampling;
import Light.Indirect;

// What a traced effect blends over frames: this frame's light, with its variance in alpha, and the view depth of each
// of its pixels; and the last frame's of both, which this frame's blends with, 0 while they hold nothing of the view
// and this frame starts them afresh.
public struct TracedBlendConstants
{
    public uint light;
    public uint depth;
    public uint lightHistory;
    public uint depthHistory;
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

public struct GIConstants
{
    public TracedDiffuseConstants diffuse;
    public TracedReflectionConstants reflections;
    // Linear, clamped to the edges: what the history is read with.
    public uint sampler;
    // The traced textures' size.
    public uint2 tracedSize;
}

public GIConstants GetGIConstants()
{
    BufferRef gi = { GetPassHeader().y };
    return gi.Load<GIConstants>(0u);
}

// The picture's pixel a traced pixel traces from: another of its footprint each frame, the same in all of them, so the
// blend over frames sees every one.
public uint2 GetTracedPixel(FrameConstants frame, uint2 traced)
{
    float2 shift = GetRandom(uint2(0u), frame.frameIndex, 0u).xy;
    return GetPicturePixel(frame, traced, GetGIConstants().tracedSize, shift);
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

// The neighbours' light averaged and brought back out of the curve, with its brightness's variance in alpha; where none
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

// What a traced effect blended last frame: its light, with the variance in alpha, and the view depth of each pixel.
public struct TracedHistory
{
    public TextureRef light;
    public TextureRef depth;
    public SamplerRef sampler;
    public uint2 size;
}

// A traced effect's history, at the traced textures' size.
public TracedHistory CreateHistory(GIConstants gi, TracedBlendConstants blend, uint2 size)
{
    TracedHistory history;
    history.light.id = blend.lightHistory;
    history.depth.id = blend.depthHistory;
    history.sampler.id = gi.sampler;
    history.size = size;
    return history;
}

// A traced pixel's view depth, kept for the next frame's history: 0 where nothing was drawn.
public void StoreViewDepth(FrameConstants frame, TracedBlendConstants blend, uint2 traced, float depth)
{
    StorageRef target = { blend.depth, 0u };
    target.Store(traced, float4(depth > 0.0 ? ToViewDepth(depth, frame.near) : 0.0, 0.0, 0.0, 0.0));
}

// How alike the history's depth must be to the surface's to be the same surface.
public static const float SameSurface = 0.9;

// How alike a history texel's view depth is to the surface's last frame: 1 the same, falling as they part.
float GetSameness(float previousDepth, float historyDepth)
{
    return exp(-abs(historyDepth - previousDepth) / max(previousDepth, Epsilon));
}

float LoadHistoryDepth(TracedHistory history, float2 uv)
{
    return history.depth.Load(GetTexelAt(saturate(uv), history.size), 0u).x;
}

// The light blended with its history, read at texture coordinates where the surface last frame at a view depth was.
// Where the texel there saw another surface, the nearest of its neighbours that saw this one is read, and failing that
// the four around it blended by hand. The history is held within spread deviations of the neighbours' mean, so light
// that changed doesn't trail, keeping blend of it, and the variance keeping varianceBlend of its own. Where the history
// saw another surface or lies off the screen, the light alone at the most variance, so the picture blurs what is new.
public float4 BlendHistory(
    TracedHistory history,
    float4 current,
    Neighbourhood neighbourhood,
    float previousDepth,
    float2 uv,
    float blend,
    float spread,
    float varianceBlend
)
{
    // Written so a depth the history never held, NaN, counts as another surface.
    float sameness = GetSameness(previousDepth, LoadHistoryDepth(history, uv));
    if (!(sameness >= SameSurface))
    {
        float2 nearest = uv;
        for (uint i = 0u; i < 9u; i++)
        {
            float2 at = uv + float2(int2(i % 3u, i / 3u) - 1) / float2(history.size);
            float candidate = GetSameness(previousDepth, LoadHistoryDepth(history, at));
            if (!(candidate > sameness))
                continue;

            sameness = candidate;
            nearest = at;
        }

        uv = nearest;
    }

    float4 previous;
    if (sameness >= SameSurface)
        previous = history.light.SampleLevel(history.sampler, uv, 0.0);
    else
    {
        float2 texel = uv * float2(history.size) - 0.5;
        float2 f = frac(texel);
        uint2 corner = uint2(max(floor(texel), 0.0));
        float4 weights = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
        previous = float4(0.0);
        float depth = 0.0;
        for (uint i = 0u; i < 4u; i++)
        {
            uint2 at = min(corner + uint2(i & 1u, i >> 1u), history.size - 1u);
            previous += history.light.Load(at, 0u) * weights[i];
            depth += history.depth.Load(at, 0u).x * weights[i];
        }

        sameness = GetSameness(previousDepth, depth);
    }

    if (!(sameness >= SameSurface) || any(saturate(uv) != uv))
        return float4(current.rgb, 1.0);

    float3 range = spread * neighbourhood.deviation;
    float3 held = clamp(previous.rgb, neighbourhood.mean - range, neighbourhood.mean + range);
    return float4(lerp(current.rgb, held, blend), max(lerp(current.a, previous.a, varianceBlend), 0.0));
}
