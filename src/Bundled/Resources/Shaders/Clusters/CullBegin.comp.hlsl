// Shares a view's detail out nearest the player first (Budget): from how many pieces each distance bucket drew last
// frame, the buckets past where the view's pieces would not fit draw coarser this frame, their clusters' error allowed
// to cover twice as many pixels again each frame they would not fit, and a little less again each frame they do, back
// down to what the settings ask. A bucket is never finer than a nearer one. Then last frame's counts are emptied for
// this frame's, and each level of the view's depth pyramid is given as many groups as it has texels (HiZArgs). One
// group.

#include "Include/Culling.hlsli"

struct PassParams
{
    // The share of the view's pieces the buckets are kept within, below 1 so a sudden turn still fits.
    float fill = 0.85;

    // The most a bucket's error is scaled by.
    float maxScale = 64.0;
};

groupshared uint Drawn[Buckets];
groupshared float Scales[Buckets];

[numthreads(64, 1, 1)]
void main(uint index : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    uint capacity = CountOf(VisiblePieces, 12u);
    Drawn[index] = Budget.Load(index * 4u);
    GroupMemoryBarrierWithGroupSync();

    // Pieces up to and including this bucket, nearest first.
    uint upTo = 0u;
    [loop] for (uint bucket = 0u; bucket <= index; bucket++)
    {
        upTo += Drawn[bucket];
    }

    // A budget never written yet reads as nothing, or as no number at all: either is the settings' own.
    float scale = asfloat(Budget.Load((Buckets + index) * 4u));
    scale = scale >= 1.0 && scale <= params.maxScale ? scale : 1.0;
    scale = (float)upTo > params.fill * (float)capacity ? min(scale * 2.0, params.maxScale) : max(scale * 0.8, 1.0);
    Scales[index] = scale;
    GroupMemoryBarrierWithGroupSync();

    float coarsest = 1.0;
    [loop] for (uint nearer = 0u; nearer <= index; nearer++)
    {
        coarsest = max(coarsest, Scales[nearer]);
    }

    Budget.Store((Buckets + index) * 4u, asuint(coarsest));
    Budget.Store(index * 4u, 0u);
    if (index < CountOf(HiZArgs, 12u))
    {
        HiZArgs.Store3(index * 12u, uint3((HiZSize(index) + 7u) / 8u, 1u));
    }
}
