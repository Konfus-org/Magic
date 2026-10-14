// Ground-truth ambient occlusion (Jimenez et al. 2016, "Practical Real-Time Strategies for Accurate Indirect
// Occlusion"): once per 2 x 2 pixels of the view, at its top-left one, how much of the hemisphere above the surface what the view drew around it
// leaves open, cosine-weighted, and the middle of what is open, the bent normal. Each slice of the view through the
// pixel is searched both ways for the highest horizon within a radius, and the arc between the two horizons, clamped to
// the hemisphere, integrated against the normal projected into the slice. The slices turn by a 4 x 4 pattern of the
// half-size image's texels that stays put from frame to frame, so the blur after it (GtaoBlur) averages sixteen
// directions into each pixel with no temporal filter to smear them. Written as the world bent normal in rgb (* 0.5 + 0.5) and visibility in a.

#include "Include/Math.hlsli"
#include "Include/Packing.hlsli"

struct PassParams
{
    float radius = 1.0;        // metres around a point that occlude it
    float slices = 2.0;        // slices of the view each pixel searches
    float steps = 4.0;         // depth samples each way along a slice
    float maxPixels = 160.0;   // the widest the search reaches on screen
};

// A 4 x 4 ordered pattern (Bayer): sixteen values that differ most between neighbours.
static const uint Pattern[16] = { 0u, 8u, 2u, 10u, 12u, 4u, 14u, 6u, 3u, 11u, 1u, 9u, 15u, 7u, 13u, 5u };

// What the slice integrates up to a horizon at angle h, for a normal at angle n in it (Jimenez et al. 2016, eq. 10).
float Arc(float h, float n)
{
    return 0.25 * (-cos(2.0 * h - n) + cos(n) + 2.0 * h * sin(n));
}

[numthreads(8, 8, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    uint width;
    uint height;
    AmbientOcclusionNoisy.GetDimensions(width, height);
    if (any(thread.xy >= uint2(width, height)))
    {
        return;
    }

    float2 pixel = float2(thread.xy * 2u) + 0.5;
    float depth = Depth.SampleLevel(DepthSampler, pixel * ViewTexel, 0.0);
    float3 worldNormal = OctDecode(Normal.SampleLevel(NormalSampler, pixel * ViewTexel, 0.0).xy * 2.0 - 1.0);
    PassParams params = LoadPassParams();
    float3 position = ViewPosition(pixel, ViewDepth(depth));
    float radiusPixels = min(params.radius * ProjScale.y * 0.5 * ViewSize.y / max(position.z, Near), params.maxPixels);
    [branch] if (depth <= 0.0 || radiusPixels < 1.0)
    {
        AmbientOcclusionNoisy[thread.xy] = float4(worldNormal * 0.5 + 0.5, 1.0);
        return;
    }

    float3 view = normalize(-position);
    float3 normal = WorldToView(worldNormal);
    uint place = (thread.x & 3u) + (thread.y & 3u) * 4u;
    float turn = ((float)Pattern[place] + 0.5) / 16.0;
    float jitter = ((float)Pattern[(place * 7u + 5u) & 15u] + 0.5) / 16.0;
    uint slices = max((uint)params.slices, 1u);
    uint steps = max((uint)params.steps, 1u);

    // A sample's horizon counts in full within most of the radius, and fades to none at its edge.
    float falloffRange = 0.615 * params.radius;
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = (params.radius - falloffRange) / falloffRange + 1.0;

    float visibility = 0.0;
    float3 bent = float3(0.0, 0.0, 0.0);
    [loop] for (uint slice = 0u; slice < slices; slice++)
    {
        float angle = ((float)slice + turn) * Pi / (float)slices;
        float2 direction = float2(cos(angle), sin(angle));
        float3 along = float3(direction.x, -direction.y, 0.0);
        float3 ortho = normalize(along - view * dot(along, view));
        float3 axis = cross(ortho, view);
        float3 projected = normal - axis * dot(normal, axis);
        float projectedLength = length(projected);
        float n = atan2(dot(projected, ortho), dot(projected, view));

        // The horizon each way, as the cosine of its angle from the view, starting at the hemisphere's edge.
        float lowAhead = cos(n + HalfPi);
        float lowBehind = cos(n - HalfPi);
        float ahead = lowAhead;
        float behind = lowBehind;
        [loop] for (uint step = 0u; step < steps; step++)
        {
            float t = ((float)step + jitter) / (float)steps;
            float2 offset = direction * max(radiusPixels * t * t, 1.0);
            [unroll] for (uint side = 0u; side < 2u; side++)
            {
                float2 at = side == 0u ? pixel + offset : pixel - offset;
                float sampled = Depth.SampleLevel(DepthSampler, at * ViewTexel, 0.0);
                if (any(at < 0.0) || any(at >= ViewSize) || sampled <= 0.0)
                {
                    continue;
                }

                float3 toSample = ViewPosition(at, ViewDepth(sampled)) - position;
                float distance = length(toSample);
                float weight = saturate(distance * falloffMul + falloffAdd);
                float cosine = dot(toSample / max(distance, 1e-6), view);
                if (side == 0u)
                {
                    ahead = max(ahead, lerp(lowAhead, cosine, weight));
                }
                else
                {
                    behind = max(behind, lerp(lowBehind, cosine, weight));
                }
            }
        }

        float h1 = n + min(FastAcos(clamp(ahead, -1.0, 1.0)) - n, HalfPi);
        float h0 = n + max(-FastAcos(clamp(behind, -1.0, 1.0)) - n, -HalfPi);
        float open = projectedLength * (Arc(h0, n) + Arc(h1, n));
        float middle = 0.5 * (h0 + h1);
        visibility += open;
        bent += (view * cos(middle) + ortho * sin(middle)) * open;
    }

    visibility = saturate(visibility / (float)slices);
    float3 bentWorld = dot(bent, bent) > 1e-8 ? ViewToWorld(normalize(bent)) : worldNormal;
    AmbientOcclusionNoisy[thread.xy] = float4(bentWorld * 0.5 + 0.5, visibility);
}
