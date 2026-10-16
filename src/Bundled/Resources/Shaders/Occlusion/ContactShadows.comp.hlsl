// Contact shadows: once per pixel of the view, a short march toward the sun through the view's depth, which catches what
// the sun's shadow maps are too coarse to: a pebble's shadow, the line where a wall meets the floor. A step that lands
// behind what was drawn there, by less than the thickness a surface is taken to have, is in shadow (Sousa 2016, "The
// Rendering of Doom"; the march jittered per pixel and frame so its steps do not band). Out of the view, nothing is
// known, and the pixel is lit.

#include "Include/Noise.hlsli"

struct PassParams
{
    float length = 0.35;     // metres the march goes toward the sun
    float thickness = 0.2;   // metres a surface in the depth is taken to be thick
    float steps = 12.0;
};

[numthreads(8, 8, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= (uint2)ViewSize))
    {
        return;
    }

    float depth = Depth.SampleLevel(DepthSampler, (float2(thread.xy) + 0.5) * ViewTexel, 0.0);
    [branch] if (depth <= 0.0 || all(SunIlluminance <= 0.0))
    {
        ContactShadows[thread.xy] = 1.0;
        return;
    }

    PassParams params = LoadPassParams();
    float2 pixel = float2(thread.xy) + 0.5;
    float3 position = ViewPosition(pixel, ViewDepth(depth));
    float3 toSun = WorldToView(-SunDirection);
    uint steps = max((uint)params.steps, 1u);
    float step = params.length / (float)steps;
    float jitter = InterleavedGradientNoise(pixel, FrameIndex);
    float lit = 1.0;
    [loop] for (uint i = 0u; i < steps; i++)
    {
        float3 along = position + toSun * (step * ((float)i + jitter));
        [branch] if (along.z <= Near)
        {
            break;
        }

        float2 at = ViewPixel(along);
        [branch] if (any(at < 0.0) || any(at >= ViewSize))
        {
            break;
        }

        float behind = along.z - ViewDepth(Depth.SampleLevel(DepthSampler, at * ViewTexel, 0.0));
        [branch] if (behind > 0.02 * along.z * step && behind < params.thickness)
        {
            // Fade out toward the march's end, so its reach does not show as an edge.
            lit = (float)i / (float)steps;
            break;
        }
    }

    ContactShadows[thread.xy] = lit;
}
