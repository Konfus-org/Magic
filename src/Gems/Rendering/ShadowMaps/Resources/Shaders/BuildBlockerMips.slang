// The min and max mips of a shadow atlas, a level a dispatch, which a wide blocker search reads (Bavoil, "Integrating
// Realistic Soft Shadows into Your Game Engine", NVIDIA, GDC 2008). Level 0 is half the atlas: each texel keeps the
// farthest and the nearest reversed depth of the two by two texels under it. The pass header's x is the atlas, y the
// level written, z the min mips and w the max mips. The atlas's sides are a power of two, so no texel is left over.
module "BuildBlockerMips";

import Core.Heap;
import Core.Pass;

[shader("compute")]
[numthreads(8, 8, 1)]
void BuildBlockerMips(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    uint level = header.y;
    StorageRef minTarget = { header.z, level };
    StorageRef maxTarget = { header.w, level };
    if (any(thread.xy >= minTarget.GetSize()))
        return;

    TextureRef atlas = { header.x };
    TextureRef minSource = { header.z };
    TextureRef maxSource = { header.w };
    uint2 first = thread.xy * 2u;
    float farthest = 1.0;
    float nearest = 0.0;
    [unroll]
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 texel = first + uint2(i & 1u, i >> 1u);
        float low = level == 0u ? atlas.Load(texel, 0u).x : minSource.Load(texel, level - 1u).x;
        float high = level == 0u ? low : maxSource.Load(texel, level - 1u).x;
        farthest = min(farthest, low);
        nearest = max(nearest, high);
    }

    minTarget.Store(thread.xy, float4(farthest, 0.0, 0.0, 0.0));
    maxTarget.Store(thread.xy, float4(nearest, 0.0, 0.0, 0.0));
}
