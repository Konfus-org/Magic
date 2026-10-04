// Ground-truth ambient occlusion (Jimenez, Wu, Pesce and Jarabo 2016): per texel of the occlusion target, a
// few slices through the view direction, each walked both ways along the screen to find the horizon the
// depth buffer raises on either side; the visible arc between the two horizons, weighted by the cosine of the
// surface's normal, is how much of the hemisphere the texel sees, and the arc's middle, summed over the
// slices, is the bent normal: where the open sky is. Nothing temporal: the slices are rotated and the steps
// offset by a fixed 4 x 4 pattern (AoCommon.hlsli) that AoBlur.comp.hlsl smooths out. Runs at the occlusion
// target's resolution (AoScale of the view's), reading the full-resolution depth at every tap, and writes
// the raw texel the blur reads. One thread per texel of the view's rectangle in the target.

#include "Include/Shade.hlsli"
#include "Lighting/AoCommon.hlsli"

#define GROUP_SIZE 8

static const float HalfPi = 1.57079633;

Texture2D<float> Depth : READ(0);
SamplerState DepthSampler : SAMPLER(0);
Texture2D Normal : READ(1);
SamplerState NormalSampler : SAMPLER(1);

[[vk::image_format("rgba16f")]]
RWTexture2D<float4> AoRaw : WRITE(0);

// The view-space position under a point of the view's rectangle (in full-resolution pixels), and its depth; 0 depth where nothing was drawn.
float3 PositionAt(float2 viewPixel, float2 targetSize, out float viewDepth)
{
    float2 clamped = clamp(viewPixel, 0.5, ViewSize - 0.5);
    float2 uv = (ViewOrigin + clamped) / targetSize;
    float depth = Depth.SampleLevel(DepthSampler, uv, 0.0);
    viewDepth = depth > 0.0 ? ViewDepth(depth) : 0.0;
    return ViewPosition(clamped, viewDepth);
}

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 aoSize = (uint2)ceil(ViewSize * AoScale);
    uint2 aoPixel = threadId.xy;
    if (any(aoPixel >= aoSize))
        return;

    uint2 aoOrigin = (uint2)floor(ViewOrigin * AoScale);
    uint targetWidth, targetHeight;
    Depth.GetDimensions(targetWidth, targetHeight);
    float2 targetSize = float2(targetWidth, targetHeight);

    // The texel's own surface, at its centre in full-resolution pixels.
    float2 viewPixel = (float2(aoPixel) + 0.5) / AoScale;
    float viewDepth;
    float3 position = PositionAt(viewPixel, targetSize, viewDepth);
    float3 worldNormal = NormalizeOrZero(Normal.SampleLevel(NormalSampler, (ViewOrigin + viewPixel) / targetSize, 0.0).xyz * 2.0 - 1.0);
    float3 normal = ViewNormal(worldNormal);
    float3 toCamera = IsOrthographic != 0u ? float3(0.0, 0.0, -1.0) : NormalizeOrZero(-position);
    float radius = AoPixelRadius(viewDepth);

    float4 result = EncodeAoRaw(1.0, viewDepth, normal);
    [branch] if (viewDepth > 0.0 && radius >= 1.0 && (AoFlags & AoEnabledFlag) != 0u)
    {
        float rotation, offset;
        AoNoise(aoPixel, rotation, offset);
        float thickness = AoParams.x * AoParams.z;
        float fallRange = max(AoParams.x - thickness, 1e-4);

        float visibility = 0.0;
        float3 bent = float3(0.0, 0.0, 0.0);
        uint slices = max(AoSlices, 1u);
        uint steps = max(AoSteps, 1u);
        [loop] for (uint slice = 0u; slice < slices; slice++)
        {
            float phi = (slice + rotation) * Pi / (float)slices;
            float2 direction = float2(cos(phi), sin(phi));
            float3 sliceDir = float3(direction, 0.0);
            float2 pixelStep = float2(direction.x, -direction.y); // screen y runs down, view y runs up

            // The slice's plane: the view direction and the screen direction, and the normal's part in it.
            float3 orthoDir = NormalizeOrZero(sliceDir - dot(sliceDir, toCamera) * toCamera);
            float3 axis = cross(orthoDir, toCamera);
            float3 projected = normal - axis * dot(normal, axis);
            float projectedLength = length(projected);
            float normalAngle = sign(dot(orthoDir, projected)) * acos(clamp(dot(projected, toCamera) / max(projectedLength, 1e-6), -1.0, 1.0));

            // The horizon on each side: the highest sample, faded back to the surface's own plane with distance.
            float horizonCos0 = -1.0, horizonCos1 = -1.0;
            [loop] for (uint step = 0u; step < steps; step++)
            {
                float along = (step + offset) / (float)steps;
                float distancePixels = max(along * along * radius, 1.0) / AoScale;
                [unroll] for (int side = 0; side < 2; side++)
                {
                    float sign2 = side == 0 ? -1.0 : 1.0;
                    float sampleDepth;
                    float3 samplePosition = PositionAt(viewPixel + sign2 * pixelStep * distancePixels, targetSize, sampleDepth);
                    float3 delta = samplePosition - position;
                    float distance = length(delta);
                    float horizonCos = sampleDepth > 0.0 ? dot(delta, toCamera) / max(distance, 1e-6) : -1.0;
                    float fade = saturate((distance - thickness) / fallRange);
                    horizonCos = lerp(horizonCos, -1.0, fade);
                    if (side == 0)
                        horizonCos0 = max(horizonCos0, horizonCos);
                    else
                        horizonCos1 = max(horizonCos1, horizonCos);
                }
            }

            float h0 = -acos(clamp(horizonCos0, -1.0, 1.0));
            float h1 = acos(clamp(horizonCos1, -1.0, 1.0));
            h0 = normalAngle + max(h0 - normalAngle, -HalfPi);
            h1 = normalAngle + min(h1 - normalAngle, HalfPi);

            float arc0 = -cos(2.0 * h0 - normalAngle) + cos(normalAngle) + 2.0 * h0 * sin(normalAngle);
            float arc1 = -cos(2.0 * h1 - normalAngle) + cos(normalAngle) + 2.0 * h1 * sin(normalAngle);
            float sliceVisibility = projectedLength * 0.25 * (arc0 + arc1);
            visibility += sliceVisibility;

            float bentAngle = (h0 + h1) * 0.5;
            bent += projectedLength * sliceVisibility * (toCamera * cos(bentAngle) + orthoDir * sin(bentAngle));
        }

        visibility = saturate(visibility / (float)slices);
        float3 bentView = NormalizeOrZero(bent);
        bentView = dot(bentView, bentView) > 0.0 ? bentView : normal;
        result = EncodeAoRaw(visibility, viewDepth, bentView);
    }

    uint rawWidth, rawHeight;
    AoRaw.GetDimensions(rawWidth, rawHeight);
    uint2 texel = aoOrigin + aoPixel;
    if (all(texel < uint2(rawWidth, rawHeight)))
        AoRaw[texel] = result;
}
