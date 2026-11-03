// The depth pyramid, a level a dispatch: level 0 copies the depth, and each level above keeps the farthest reversed
// depth of the texels below it, the last row and column of an odd level folded in so no texel goes uncovered. The
// pass header's y is the level written.
module "WorldRenderer/BuildHzb";

import Core.Heap;
import Core.Pass;
import Core.Frame;

[shader("compute")]
[numthreads(8, 8, 1)]
void BuildHzb(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint level = GetPassHeader().y;
    StorageRef target = { frame.hzb, level };
    uint2 size = target.GetSize();
    if (any(thread.xy >= size))
        return;

    if (level == 0u)
    {
        TextureRef depth = { frame.depth };
        target.Store(thread.xy, float4(depth.Load(thread.xy, 0u).x, 0.0, 0.0, 0.0));
        return;
    }

    TextureRef source = { frame.hzb };
    uint2 sourceSize = source.GetSize(level - 1u);
    uint2 first = thread.xy * 2u;
    uint2 count = uint2(2u, 2u);
    if ((sourceSize.x & 1u) != 0u && thread.x == size.x - 1u)
        count.x = 3u;

    if ((sourceSize.y & 1u) != 0u && thread.y == size.y - 1u)
        count.y = 3u;

    float farthest = 1.0;
    for (uint y = 0u; y < count.y; y++)
    {
        for (uint x = 0u; x < count.x; x++)
        {
            uint2 texel = min(first + uint2(x, y), sourceSize - 1u);
            farthest = min(farthest, source.Load(texel, level - 1u).x);
        }
    }

    target.Store(thread.xy, float4(farthest, 0.0, 0.0, 0.0));
}
