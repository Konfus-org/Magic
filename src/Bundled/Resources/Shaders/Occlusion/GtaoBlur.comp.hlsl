// The ambient occlusion GTAO found at half size, each pixel the average of the 4 x 4 texels around it, so the sixteen
// slice directions of its pattern meet in every pixel; a texel counts less the further the depth it was found at is
// from the pixel's, so occlusion does not bleed across an edge onto what is in front or behind. The texels a group's
// pixels average are read once for the group, each with the view depth it was found at.

// Pixels a side of a group, and the half-size texels a side its pixels' 4 x 4s cover.
static const uint Group = 16u;
static const uint Span = Group / 2u + 3u;

// Each texel's view depth, negative where nothing was drawn, and what GTAO found there.
groupshared float SharedDepth[Span * Span];
groupshared float4 SharedOcclusion[Span * Span];

[numthreads(16, 16, 1)]
void main(uint3 thread : SV_DispatchThreadID, uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    uint width;
    uint height;
    AmbientOcclusionNoisy.GetDimensions(width, height);
    float2 size = float2(width, height);
    int2 first = int2(group.xy * (Group / 2u)) - 2;
    for (uint load = index; load < Span * Span; load += Group * Group)
    {
        float2 texel = float2(clamp(first + int2(load % Span, load / Span), int2(0, 0), int2(size) - 1));
        float sampled = Depth.SampleLevel(DepthSampler, (texel * 2.0 + 0.5) * ViewTexel, 0.0);
        SharedDepth[load] = sampled > 0.0 ? ViewDepth(sampled) : -1.0;
        SharedOcclusion[load] = AmbientOcclusionNoisy.SampleLevel(AmbientOcclusionNoisySampler, (texel + 0.5) / size, 0.0);
    }

    GroupMemoryBarrierWithGroupSync();
    if (any(thread.xy >= (uint2)ViewSize))
    {
        return;
    }

    float2 pixel = float2(thread.xy) + 0.5;
    float depth = Depth.SampleLevel(DepthSampler, pixel * ViewTexel, 0.0);
    [branch] if (depth <= 0.0)
    {
        AmbientOcclusion[thread.xy] = AmbientOcclusionNoisy.SampleLevel(AmbientOcclusionNoisySampler, pixel * ViewTexel, 0.0);
        return;
    }

    int2 centre = int2(thread.xy / 2u) - first;
    float viewDepth = ViewDepth(depth);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float weights = 0.0;
    [unroll] for (int y = -2; y < 2; y++)
    {
        [unroll] for (int x = -2; x < 2; x++)
        {
            uint at = (uint)((centre.y + y) * (int)Span + centre.x + x);
            float sampled = SharedDepth[at];
            float weight = sampled >= 0.0 ? saturate(1.0 - abs(sampled - viewDepth) / (0.05 * viewDepth)) : 0.0;
            sum += SharedOcclusion[at] * weight;
            weights += weight;
        }
    }

    // A pixel no texel near enough in depth vouches for, at an edge the half size missed, keeps its nearest.
    AmbientOcclusion[thread.xy] = weights > 1e-4 ? sum / weights : SharedOcclusion[centre.y * (int)Span + centre.x];
}
