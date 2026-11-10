// What the light traced at half the picture's size shares, the diffuse and the reflections alike: which pixel of each
// 2 by 2 traces this frame, and its history found where the surface was last frame.
module "Scene/Traced";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Light.Indirect;

// The picture's pixel a traced pixel traces from: another of its 2 by 2 each frame, the same in all of them, so the
// blend over frames sees every one.
public uint2 GetTracedPixel(FrameConstants frame, uint2 traced)
{
    uint2 shift = uint2(GetRandom(uint2(0u), frame.frameIndex, 0u).xy * float(TracedScale));
    return min(traced * TracedScale + shift, uint2(frame.width, frame.height) - 1u);
}

// The mean and deviation of the 3 by 3 traced pixels' light around one.
public void GetNeighbourhood(TextureRef light, uint2 traced, uint2 size, out float3 mean, out float3 deviation)
{
    float3 sum = float3(0.0);
    float3 squares = float3(0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 neighbour = uint2(clamp(int2(traced) + int2(x, y), int2(0), int2(size) - 1));
            float3 color = light.Load(neighbour, 0u).rgb;
            sum += color;
            squares += color * color;
        }
    }

    mean = sum / 9.0;
    deviation = sqrt(max(squares / 9.0 - mean * mean, 0.0));
}

// What a traced effect blended last frame: its light, with the variance in alpha, and the view depth of each pixel.
public struct TracedHistory
{
    public TextureRef light;
    public TextureRef depth;
    public SamplerRef sampler;
    public uint2 size;
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
    return history.depth.Load(min(uint2(saturate(uv) * float2(history.size)), history.size - 1u), 0u).x;
}

// The history's light at texture coordinates where a surface last frame at a view depth was, and how alike the depth
// found there is, from 0 to 1. Where the texel saw another surface, the nearest of its neighbours that saw this one is
// taken, and failing that the four around it blended by hand. The coordinates move to where the light was read.
public float4 SampleHistory(TracedHistory history, float previousDepth, inout float2 uv, out float sameness)
{
    // Written so a depth the history never held, NaN, counts as another surface.
    sameness = GetSameness(previousDepth, LoadHistoryDepth(history, uv));
    if (!(sameness >= SameSurface))
    {
        float2 nearest = uv;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                float2 at = uv + float2(x, y) / float2(history.size);
                float candidate = GetSameness(previousDepth, LoadHistoryDepth(history, at));
                if (candidate > sameness)
                {
                    sameness = candidate;
                    nearest = at;
                }
            }
        }

        uv = nearest;
    }

    if (sameness >= SameSurface)
        return history.light.SampleLevel(history.sampler, uv, 0.0);

    float2 texel = uv * float2(history.size) - 0.5;
    float2 f = frac(texel);
    uint2 corner = uint2(max(floor(texel), 0.0));
    float4 weights = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
    float4 previous = float4(0.0);
    float depth = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 at = min(corner + uint2(i & 1u, i >> 1u), history.size - 1u);
        previous += history.light.Load(at, 0u) * weights[i];
        depth += history.depth.Load(at, 0u).x * weights[i];
    }

    sameness = GetSameness(previousDepth, depth);
    return previous;
}

// The light blended with its history: the history held within spread deviations of the neighbours' mean, so light
// that changed doesn't trail, keeping blend of it, and the variance keeping varianceBlend of its own. Where the history
// saw another surface or lies off the screen, the light alone at the most variance, so the picture blurs what is new.
public float4 BlendHistory(
    float4 current,
    float4 previous,
    float sameness,
    float2 uv,
    float3 mean,
    float3 deviation,
    float blend,
    float spread,
    float varianceBlend
)
{
    if (!(sameness >= SameSurface) || any(saturate(uv) != uv))
        return float4(current.rgb, 1.0);

    float3 held = clamp(previous.rgb, mean - spread * deviation, mean + spread * deviation);
    return float4(lerp(current.rgb, held, blend), max(lerp(current.a, previous.a, varianceBlend), 0.0));
}
