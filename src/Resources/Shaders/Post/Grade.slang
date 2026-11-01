// The view graded: how saturated it is and how much contrast it has about middle grey, each made more (up to 1, twice)
// or less (down to -1: grey, or flat), a tint over it and a vignette darkening its corners. At zeros and a white tint it
// changes nothing. Core's Grade's, member for member.

module "Post/Grade";

import Core.Color;
import Core.Post;

public struct Grade : IPixelEffect
{
    Color tint;              // multiplied over it, linear
    float saturation;        // 0 as it is
    float contrast;          // about middle grey, 0 as it is
    float vignette;          // how dark the corners go, 0 none

    public float3 Apply(float3 color, PixelInputs input)
    {
        float grey = GetLuminance(color);
        color = lerp(float3(grey, grey, grey), color, 1.0 + saturation);
        color = (color - 0.18) * (1.0 + contrast) + 0.18;
        color *= tint.rgb;
        float2 fromMiddle = input.uv - 0.5;
        color *= 1.0 - vignette * smoothstep(0.2, 0.8, dot(fromMiddle, fromMiddle) * 2.0);
        return saturate(color);
    }
}
