// Builds a level of the view's depth pyramid (hiZ), a thread a texel, a dispatch a level: level 0 from the view's
// depth, each level after from the one before, every texel the farthest of the four under it, which, reverse-Z, is the
// least. A level whose size is odd covers its last row or column with the texel before it, so nothing is left out.

#include "Rendering/Include/Culling.hlsli"

struct HiZConstants
{
    uint view;
    uint depth;              // the view's depth
    uint hiZ;
    uint level;              // the level built
};

CONSTANTS(HiZConstants, c);

// The farthest depth of four texels of the level below, level size texels from offset.
float FarthestBelow(uint2 under, uint2 size, uint offset)
{
    uint2 last = size - 1u;
    uint2 a = min(under, last);
    uint2 b = min(under + uint2(1u, 0u), last);
    uint2 d = min(under + uint2(0u, 1u), last);
    uint2 e = min(under + 1u, last);
    return min(min(HiZAt(c.hiZ, size, offset, a), HiZAt(c.hiZ, size, offset, b)), min(HiZAt(c.hiZ, size, offset, d), HiZAt(c.hiZ, size, offset, e)));
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    LoadView(c.view);
    uint2 size;
    uint offset;
    HiZLevel(c.level, size, offset);
    if (any(id.xy >= size))
    {
        return;
    }

    uint2 under = id.xy * 2u;
    float farthest;
    [branch] if (c.level == 0u)
    {
        uint2 last = (uint2)View.viewSize - 1u;
        Texture2D depth = TEXTURE(c.depth);
        farthest = min(
            min(depth.Load(int3(min(under, last), 0)).r, depth.Load(int3(min(under + uint2(1u, 0u), last), 0)).r),
            min(depth.Load(int3(min(under + uint2(0u, 1u), last), 0)).r, depth.Load(int3(min(under + 1u, last), 0)).r));
    }
    else
    {
        uint2 below;
        uint offsetBelow;
        HiZLevel(c.level - 1u, below, offsetBelow);
        farthest = FarthestBelow(under, below, offsetBelow);
    }

    BUFFER(c.hiZ).Store((offset + id.y * size.x + id.x) * 4u, asuint(farthest));
}
