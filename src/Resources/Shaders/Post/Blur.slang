// A box blur, "radius" texels each side of a pixel, each way, up to MostRadius: the picture blurred across into the
// effect's one level, then that blurred down, two passes of 2r + 1 reads where one would take (2r + 1)².
module "Post/Blur";

import Post.IPixelEffect;

// The widest blur, in texels each side, which keeps a pass's reads few.
static const float MostRadius = 32.0;

public struct Blur : IPixelEffect
{
    public static const uint Levels = 1;

    public float radius;

    public override float4 Level(PixelEffectInputs inputs)
    {
        int reach = int(clamp(radius, 0.0, MostRadius));
        float3 sum = float3(0.0);
        for (int x = -reach; x <= reach; x++)
            sum += inputs.picture.Load(GetTexel(inputs, int2(x, 0)), 0u).rgb;

        return float4(sum / float(2 * reach + 1), 1.0);
    }

    public float3 Apply(float3 color, PixelEffectInputs inputs)
    {
        int reach = int(clamp(radius, 0.0, MostRadius));
        float3 sum = float3(0.0);
        for (int y = -reach; y <= reach; y++)
            sum += inputs.target.Load(GetTexel(inputs, int2(0, y)), 0u).rgb;

        return sum / float(2 * reach + 1);
    }
}

// The texel offset from the pixel, held to the edge.
uint2 GetTexel(PixelEffectInputs inputs, int2 offset)
{
    return uint2(clamp(int2(inputs.pixel) + offset, int2(0), int2(inputs.size) - 1));
}
