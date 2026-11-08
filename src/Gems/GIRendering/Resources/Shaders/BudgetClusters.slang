// The cluster budget, first of a frame's passes, one thread. Last frame's count of the visible list's rows its
// clusters asked for moves a level: up fast while the list was nearly full, down slowly once it was well under it,
// still in between. The level pushes the impostor handoff out, its pixel radius up and its distance in, so what is
// smallest on screen and farthest goes to its impostor first, and past that raises the error a cluster may show. The
// frame's constants take the handoff and the error it gives, so every pass after reads the same, and the counts are
// zeroed for the cull. The pass header's z is the budget buffer, which holds the level.
module "BudgetClusters";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Visibility;

// The shares of the visible list over which the level rises and under which it falls.
static const float FullShare = 0.9;
static const float RoomyShare = 0.6;

// How far the level moves a frame each way.
static const float StepUp = 0.25;
static const float StepDown = 0.02;

// The levels that each double the handoff's pixel radius and halve its distance, and the highest, the levels past
// the handoff's each doubling the error a cluster may show.
static const float HandoffLevels = 4.0;
static const float MaxLevel = 6.0;

[shader("compute")]
[numthreads(1, 1, 1)]
void BudgetClusters()
{
    PassHeader header = GetPassHeader();
    BufferRef constants = { header.x };
    FrameConstants frame = constants.Load<FrameConstants>(0u);
    BufferRef counts = { frame.counts };
    BufferRef budget = { header.z };

    // A level that is not a number in range is a buffer never written: none.
    float level = budget.Load<float>(0u);
    if (!(level >= 0.0 && level <= MaxLevel))
        level = 0.0;

    float share = float(counts.Load<uint>(CountWantedRows)) / float(frame.maxVisibleClusters);
    if (share > FullShare)
        level = min(level + StepUp, MaxLevel);
    else if (share < RoomyShare)
        level = max(level - StepDown, 0.0);

    budget.Store<float>(0u, level);

    float handoff = exp2(min(level, HandoffLevels));
    frame.impostorPixels *= handoff;
    frame.impostorDistance /= handoff;
    frame.errorThreshold *= exp2(max(level - HandoffLevels, 0.0));
    constants.Store<FrameConstants>(0u, frame);

    for (uint offset = 0u; offset < counts.size; offset += 4u)
    {
        counts.Store<uint>(offset, 0u);
    }
}
