// A vignette: the picture darkened towards its corners, as much as "strength" says, 0 for none and 1 for black corners.
module "Post/Vignette";

import Post.IPixelEffect;

public struct Vignette : IPixelEffect
{
    public float strength;

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        float2 centred = inputs.uv * 2.0 - 1.0;
        float falloff = 1.0 - smoothstep(0.5, 1.6, dot(centred, centred));
        return color * lerp(1.0, falloff, saturate(strength));
    }
}
