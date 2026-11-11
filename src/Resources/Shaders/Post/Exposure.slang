// The exposure, metered from the picture and adapted to over time, as an eye adapts: the picture multiplied by what
// brings the average of its log luminance to the key (Reinhard et al., "Photographic Tone Reproduction for Digital
// Images", 2002), with the stops of "compensation" added. Its target's levels average each pixel's log luminance, black
// pixels left out, down to one texel, which moves towards the frame's average from last frame's. Over a range wide
// enough for light in physical units.
module "Post/Exposure";

import Core.Color;
import Core.Frame;
import Post.IPixelEffect;

// What the average luminance is brought to, and how fast the adapted luminance follows it, per second.
static const float Key = 0.115;
static const float AdaptionRate = 1.0;

// The log2 luminance the average spans, and the luminance under which a pixel is black and left out.
static const float LeastLog = -10.0;
static const float MostLog = 20.0;
static const float BlackLuminance = 0.001;

public struct Exposure : IPixelEffect
{
    public static const uint Levels = 16;
    public static const uint History = 1;

    public float compensation;

    // Each texel of a level holds the average log luminance of the pixels under it times the share of them that aren't
    // black, and that share. The last, one texel, also holds the log luminance adapted to, in z and w.
    public override float4 Level(PixelEffectInputs inputs)
    {
        if (inputs.level == 0u)
            return GetLogLuminance(inputs);

        float2 average = GetCoveredAverage(inputs);
        if (any(inputs.size != 1u))
            return float4(average, 0.0, 0.0);

        // The adapted log luminance moved towards the frame's, or set to it with no history. A half float alone would
        // round a frame's small step away, so it is kept as one and what that one rounded off. The one is rounded to
        // a 64th, which a half holds exactly under 32. The compiler folds a half round trip away, which leaves nothing
        // rounded off.
        float frameLog = average.y > 0.0 ? average.x / average.y : LeastLog;
        float4 last = inputs.history.Load(uint2(0u), inputs.level);
        float adapted = inputs.hasHistory
            ? lerp(last.z + last.w, frameLog, 1.0 - exp(-GetFrame().deltaSec * AdaptionRate))
            : frameLog;
        float high = round(adapted * 64.0) / 64.0;
        return float4(average, high, adapted - high);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        uint last = min(firstbithigh(max(inputs.size.x, inputs.size.y)), Levels - 1u);
        float4 metered = inputs.target.Load(uint2(0u), last);
        return color * Key * exp2(compensation - metered.z - metered.w);
    }
}

// The texel's average of the level below over the share of the picture it covers, each texel below weighted by how much
// of it that is: a level half the one below, rounded down, covers two of it and a part of a third, so every texel of a
// level covers the same share and none of an odd size is left out.
float2 GetCoveredAverage(PixelEffectInputs inputs)
{
    uint2 below = inputs.target.GetSize(inputs.level - 1u);
    float2 scale = float2(below) / float2(inputs.size);
    float2 start = float2(inputs.pixel) * scale;
    float2 end = start + scale;
    float2 sum = float2(0.0);
    for (uint y = uint(start.y); y < below.y && float(y) < end.y; y++)
    {
        for (uint x = uint(start.x); x < below.x && float(x) < end.x; x++)
        {
            float2 overlap = min(end, float2(x, y) + 1.0) - max(start, float2(x, y));
            sum += inputs.target.Load(uint2(x, y), inputs.level - 1u).xy * overlap.x * overlap.y;
        }
    }

    return sum / (scale.x * scale.y);
}

// The pixel's log luminance, from the average of the 2 by 2 it is in, as clamped to the range, and 1, or nothing when
// it is black.
float4 GetLogLuminance(PixelEffectInputs inputs)
{
    uint2 block = inputs.pixel & ~1u;
    uint2 last = inputs.size - 1u;
    float3 color = float3(0.0);
    for (uint i = 0u; i < 4u; i++)
        color += inputs.picture.Load(min(block + uint2(i & 1u, i >> 1u), last), 0u).rgb;

    float luminance = GetLuminance(color * 0.25);
    return luminance <= BlackLuminance ? float4(0.0) : float4(clamp(log2(luminance), LeastLog, MostLog), 1.0, 0.0, 0.0);
}
