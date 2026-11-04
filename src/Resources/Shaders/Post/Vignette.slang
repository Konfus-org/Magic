// A vignette: the picture darkened towards its corners.
module "Post/Vignette";

import Post.IPixelEffect;

public struct Vignette : IPixelEffect
{
    public static float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        float2 centred = inputs.uv * 2.0 - 1.0;
        float falloff = 1.0 - smoothstep(0.5, 1.6, dot(centred, centred));
        return color * falloff;
    }
}
