// The post effect that leaves the picture as it is: what an empty slot of a PostProcessing runs.
module "Post/NoEffect";

import Post.IPixelEffect;

public struct NoEffect : IPixelEffect
{
    public static float3 Apply(float3 color, PixelEffectInputs inputs) { return color; }
}
