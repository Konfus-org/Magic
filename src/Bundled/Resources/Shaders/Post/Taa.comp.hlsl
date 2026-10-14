// Temporal anti-aliasing and upscaling (Karis 2014, "High Quality Temporal Supersampling"), a thread a pixel of the
// view as it is shown (Taa): the view's pixels about where it falls are gathered, each weighed by how near the place
// its sample really was (the view is drawn a sub-pixel off each frame, by Jitter) by a Blackman-Harris window, and
// blended into what Taa held there last frame, found by the velocity of the nearest surface about it (the sky's from
// the camera's turn) and read through a Catmull-Rom filter, so it stays sharp. What was there is first clipped toward
// the colours about the pixel now, their mean and spread in YCoCg (Salvi 2016), so what moved or came into sight ghosts
// no further than what is there now. Both are weighed by their brightness as they blend, so a lone bright sample does
// not flicker. What Taa held where nothing was before, or where the view first comes, is drawn from the view alone.

struct PassParams
{
    // The share of the new frame a pixel takes where a sample of it falls on the pixel's centre; less where none does.
    float feedback = 0.1;

    // The spread about the mean, in standard deviations, past which what was there is clipped.
    float clipSpread = 1.0;
};

float Luma(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

float3 ToYCoCg(float3 color)
{
    return float3(dot(color, float3(0.25, 0.5, 0.25)), dot(color, float3(0.5, 0.0, -0.5)), dot(color, float3(-0.25, 0.5, -0.25)));
}

float3 FromYCoCg(float3 color)
{
    return float3(color.x + color.y - color.z, color.x + color.z, color.x - color.y - color.z);
}

// The colour last frame left at a place, through a Catmull-Rom filter of five bilinear taps, its corners left out
// (Jimenez 2016, "Filmic SMAA").
float3 HistoryAt(float2 uv)
{
    float2 size = OutputSize;
    float2 position = uv * size;
    float2 centre = floor(position - 0.5) + 0.5;
    float2 f = position - centre;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 at0 = (centre - 1.0) / size;
    float2 at3 = (centre + 2.0) / size;
    float2 at12 = (centre + w2 / w12) / size;
    float3 color = TaaHistory.SampleLevel(TaaHistorySampler, float2(at12.x, at0.y), 0.0).rgb * (w12.x * w0.y)
        + TaaHistory.SampleLevel(TaaHistorySampler, float2(at0.x, at12.y), 0.0).rgb * (w0.x * w12.y)
        + TaaHistory.SampleLevel(TaaHistorySampler, at12, 0.0).rgb * (w12.x * w12.y)
        + TaaHistory.SampleLevel(TaaHistorySampler, float2(at3.x, at12.y), 0.0).rgb * (w3.x * w12.y)
        + TaaHistory.SampleLevel(TaaHistorySampler, float2(at12.x, at3.y), 0.0).rgb * (w12.x * w3.y);
    float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return max(color / weight, 0.0);
}

// What was there, clipped toward the box about the mean: along the line to the mean, to where it leaves the box.
float3 Clip(float3 history, float3 mean, float3 extent)
{
    float3 offset = history - mean;
    float3 units = abs(offset / max(extent, 1e-5));
    float most = max(units.x, max(units.y, units.z));
    return most > 1.0 ? mean + offset / most : history;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)OutputSize))
    {
        return;
    }

    PassParams params = LoadPassParams();
    float2 uv = ((float2)id.xy + 0.5) / OutputSize;
    float2 place = uv * ViewSize;
    int2 centre = (int2)floor(place);
    int2 last = (int2)ViewSize - 1;

    // A view pixel's sample shows the scene a jitter away from its centre: the jitter is in clip space, y up.
    float2 jitter = Jitter * float2(0.5, -0.5) * ViewSize;
    float3 sum = float3(0.0, 0.0, 0.0);
    float total = 0.0;
    float peak = 0.0;
    float3 moment1 = float3(0.0, 0.0, 0.0);
    float3 moment2 = float3(0.0, 0.0, 0.0);
    float nearest = 0.0;
    int2 nearestAt = clamp(centre, int2(0, 0), last);
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            int2 at = clamp(centre + int2(x, y), int2(0, 0), last);
            float3 color = max(Hdr.Load(int3(at, 0)).rgb, 0.0);
            float2 apart = (float2)at + 0.5 - jitter - place;

            // Blackman-Harris, as a Gaussian fitted to it, a pixel of the view wide.
            float weight = exp(-2.29 * dot(apart, apart));
            float luma = 1.0 / (1.0 + Luma(color));
            sum += color * weight * luma;
            total += weight * luma;
            peak = max(peak, weight);
            float3 ycocg = ToYCoCg(color);
            moment1 += ycocg;
            moment2 += ycocg * ycocg;

            // Reverse-Z: the nearest is the most.
            float depth = Depth.Load(int3(at, 0));
            if (depth > nearest)
            {
                nearest = depth;
                nearestAt = at;
            }
        }
    }

    float3 current = sum / max(total, 1e-6);

    // Where the pixel was last frame: its nearest surface's velocity, or for the sky, the camera's turn alone.
    float2 velocity;
    if (nearest > 0.0)
    {
        velocity = Velocity.Load(int3(nearestAt, 0)).xy;
    }
    else
    {
        float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        float4 before = mul(PrevViewProj, float4(ViewToWorld(float3(ndc / ProjScale, 1.0)), 0.0));
        velocity = (ndc - before.xy / max(before.w, 1e-6)) * float2(0.5, -0.5);
    }

    float2 historyUv = uv - velocity;
    float3 result = current;
    if (all(historyUv > 0.0) && all(historyUv < 1.0))
    {
        float3 mean = moment1 / 9.0;
        float3 spread = sqrt(max(moment2 / 9.0 - mean * mean, 0.0)) * params.clipSpread;
        float3 history = FromYCoCg(Clip(ToYCoCg(HistoryAt(historyUv)), mean, spread));
        float share = max(params.feedback * peak, 0.02);
        float currentWeight = share / (1.0 + Luma(current));
        float historyWeight = (1.0 - share) / (1.0 + Luma(history));
        result = (current * currentWeight + history * historyWeight) / (currentWeight + historyWeight);
    }

    // Never, as no pixel is that far, but it keeps every read bound with its sampler, though read texel by texel.
    float kept = id.x > 0x7FFFFFFFu ? KeepReadsAlive(uv) : 0.0;
    Taa[id.xy] = float4(result + kept, 1.0);
}
