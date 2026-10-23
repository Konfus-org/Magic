// Shares a view's detail out nearest the player first (budget): from how many pieces each distance bucket drew last
// frame, the buckets past where the view's pieces would not fit draw coarser this frame, their clusters' error allowed
// to cover twice as many pixels again each frame they would not fit, and a little less again each frame they fit with
// room to spare, back down to what the settings ask; in between they hold, so detail settles rather than going back and
// forth. A bucket is never finer than a nearer one. Then last frame's counts are emptied for this frame's. One group.

#include "Rendering/Include/Culling.hlsli"

CONSTANTS(CullConstants, c);

// The share of the view's pieces the buckets are kept within, below 1 so a sudden turn still fits.
static const float Fill = 0.85;

// The share below which a bucket drawn coarser is drawn finer again: well under Fill, so that once a bucket is fine
// enough to fit, it is not made coarse again by the detail it gets back.
static const float Refill = 0.6;

// The most a bucket's error is scaled by.
static const float MaxScale = 64.0;

groupshared uint Drawn[Buckets];
groupshared float Scales[Buckets];

[numthreads(64, 1, 1)]
void main(uint index : SV_GroupIndex)
{
    LoadView(c.view);
    uint capacity = CountOf(BUFFER(c.pieces), 12u);
    Drawn[index] = BUFFER(c.budget).Load(index * 4u);
    GroupMemoryBarrierWithGroupSync();

    // Pieces up to and including this bucket, nearest first.
    uint upTo = 0u;
    [loop] for (uint bucket = 0u; bucket <= index; bucket++)
    {
        upTo += Drawn[bucket];
    }

    // A budget never written yet reads as nothing, or as no number at all: either is the settings' own.
    float scale = asfloat(BUFFER(c.budget).Load((Buckets + index) * 4u));
    scale = scale >= 1.0 && scale <= MaxScale ? scale : 1.0;
    float used = (float)upTo / (float)max(capacity, 1u);
    scale = used > Fill ? min(scale * 2.0, MaxScale) : used < Refill ? max(scale * 0.8, 1.0) : scale;
    Scales[index] = scale;
    GroupMemoryBarrierWithGroupSync();

    float coarsest = 1.0;
    [loop] for (uint nearer = 0u; nearer <= index; nearer++)
    {
        coarsest = max(coarsest, Scales[nearer]);
    }

    BUFFER(c.budget).Store((Buckets + index) * 4u, asuint(coarsest));
    BUFFER(c.budget).Store(index * 4u, 0u);
    if (index <= CountPiecesFirst)
    {
        BUFFER(c.counts).Store(index * 4u, 0u);
    }
}
