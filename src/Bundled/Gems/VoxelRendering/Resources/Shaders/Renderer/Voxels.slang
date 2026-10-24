// The world as voxels around the player's camera, kept from frame to frame and changed only where something did, and
// the cones traced through it for shadows and occlusion.
//
// The voxels are a clipmap: levels of 128 voxels a side, each twice the size of the one before, all centred on the
// player's camera and fixed to the world, which moves by whole bricks of 8 voxels as the camera does. A level is
// stored wrapped, a voxel at its world place modulo 128, so a move leaves every voxel where it is and only the bricks
// that came in are voxelized. Each voxel keeps which of its eight halves hold a surface (the sub-voxel bits), and from
// them how much it hides light along each axis (its directional opacity, Crassin et al. 2011's anisotropic voxel): one
// along an axis whose every column of halves holds one, as a wall seen face on, less seen along it. Each level
// voxelizes the clusters of the cut whose error is half its voxel, so a coarse level never reads finer geometry than
// it can hold. Opacity is one 3D texture, the levels stacked along z, each with a slice of padding above
// and below that repeats its other end, so a sample filters across the wrap in every axis.
//
// The passes, of the whole frame, in order:
//
//   MarkVoxelBricks       an instance slot a thread: one whose sphere or look changed marks the bricks of its old and
//                         new spheres; then a brick a thread: one that came into its level's window is marked; its
//                         last group takes the marked bricks, finest level first, up to the frame's budget
//   ListVoxelJobs         an instance slot a thread: one reaching a brick taken lists the cut's clusters for each
//                         level, in jobs of 64; then a word a thread empties the taken bricks' bits
//   VoxelizeClusters      a group a job, a thread a cluster: its triangles' halves set, conservatively (Akenine-Möller
//                         2001's overlap, column by column along the triangle's dominant axis), in the taken bricks
//   ResolveVoxelOpacity   a group a taken brick, a thread a voxel: its directional opacity from its bits
//
// What is not taken in a frame stays marked for the next, so detail is late rather than lost.

module "Renderer/Voxels";

import Core.Compute;
import Renderer.Culling;
__exported import Renderer.Pieces;

// Voxels a level's window is a side, bricks a side, and voxels a brick is a side.
public static const uint VoxelWindow = 128u;
public static const uint VoxelBricks = 16u;
public static const uint VoxelBrick = 8u;

// Bricks a level holds, and slices a level takes in the opacity texture: its window and a slice of padding either side.
public static const uint LevelBricks = VoxelBricks * VoxelBricks * VoxelBricks;
public static const uint LevelSlices = VoxelWindow + 2u;

// Words of sub-voxel bits a brick holds: a byte a voxel.
static const uint BrickWords = VoxelBrick * VoxelBrick * VoxelBrick / 4u;

// A level's flags: its window was put in place anew, so every brick of it is marked.
public static const uint VoxelLevelReset = 1u;

// The narrowest a cone is followed at, as the tangent of its half-angle: a cone narrower, a sun's, is followed as this
// wide, so its steps grow with distance.
public static const float MinConeSlope = 1.0 / 16.0;

// Steps a cone takes at most, and the transmittance below which it stops as blocked.
static const uint MaxConeSteps = 96u;
static const float ConeBlocked = 0.01;

// One level of the clipmap, the renderer's GpuVoxelLevel member for member: its window's lowest corner in voxels of
// the world (a whole number of bricks), the metres a voxel is, where the corner was the frame before, and its flags.
public struct GpuVoxelLevel
{
    float3 origin;
    float voxelSize;
    float3 lastOrigin;
    uint flags;
}

// The voxels as a pass reads them: Core's VoxelScene, member for member.
public struct VoxelScene
{
    VolumeRef opacity;               // each voxel's opacity along x, y and z, the levels stacked along z
    ArrayRef<GpuVoxelLevel> levels;
}

// A cone through the voxels, from where it starts in the world along its direction, widening by tanHalfAngle, from
// start metres along it to end; leaving a surface whose normal is normal, which each sample stands off by a voxel of
// the level it reads (none for a cone from no surface).
public struct Cone
{
    float3 origin;
    float3 direction;
    float3 normal;
    float tanHalfAngle;
    float start;
    float end;
}

// Whatever the passes that change the voxels share: the renderer's VoxelCulling.
struct VoxelizeConstants
{
    VoxelScene scene;
    ArrayRef<uint> bits;          // each voxel's sub-voxel bits, a byte a voxel, brick by brick
    ArrayRef<uint> marked;        // a bit a brick, marked to be voxelized
    ArrayRef<uint> taken;         // a bit a brick, taken this frame
    ArrayRef<uint> takenList;     // the bricks taken this frame, by their place among every level's
    ArrayRef<float4> seenSpheres; // each instance slot's sphere in the world as the voxels last saw it, 0 for none
    ArrayRef<uint> seenLooks;     // and its look's key (GetLookKey)
    ArrayRef<uint4> jobs;         // a run of an instance's clusters: slot, first cluster, count, level
    ArrayRef<uint> counts;        // the counts below
    ArrayRef<uint3> args;         // the groups ListVoxelJobs, VoxelizeClusters and ResolveVoxelOpacity run
    uint brickBudget;             // bricks taken a frame at most
}

// Counts, a uint each.
static const uint CountTaken = 0u;
static const uint CountJobs = 1u;
static const uint CountGroupsDone = 2u;

groupshared uint LaneMarked[64];

public extension VoxelScene
{
    public property uint levelCount
    {
        get { return levels.count; }
    }

    // The finest level whose window holds a point of the world a voxel inside its edges, where a sample of it filters
    // only voxels of the window; levelCount for none.
    public uint GetFinestLevel(float3 position)
    {
        [loop] for (uint level = 0u; level < levelCount; level++)
        {
            GpuVoxelLevel row = levels[level];
            float3 inWindow = position / row.voxelSize - row.origin;
            if (all(inWindow >= 1.0) && all(inWindow < (float)VoxelWindow - 1.0))
            {
                return level;
            }
        }

        return levelCount;
    }

    // How much a level's voxels hide light along x, y and z at a point of the world, filtered.
    public float3 SampleOpacity(uint level, float3 position)
    {
        float3 voxel = position / levels[level].voxelSize;
        float stored = voxel.z - (float)VoxelWindow * floor(voxel.z / (float)VoxelWindow);
        float slices = (float)(levelCount * LevelSlices);
        float3 uvw = float3(voxel.xy / (float)VoxelWindow, ((float)(level * LevelSlices) + 1.0 + stored) / slices);
        return opacity.SampleLevel(World.materialSampler, uvw, 0.0).rgb;
    }

    // The share of light a cone lets through, 1 open to 0 blocked (Crassin et al. 2011): from start to end, each step
    // read at the level whose voxels are as wide as the cone there, blended between the two levels about it, never
    // finer than the finest window holding the point, and composited front to back, its opacity along the cone the
    // axes' weighted by the direction's squares, corrected for the step's length against the voxel's. Each sample
    // stands a voxel of its level off the surface the cone leaves, as a voxel that coarse holds the surface itself,
    // which a cone grazing it would read as in its way. A cone that leaves the coarsest window is open from there.
    public float TraceTransmittance(Cone cone)
    {
        float finestSize = levels[0u].voxelSize;
        float slope = max(cone.tanHalfAngle, MinConeSlope);
        float3 weights = cone.direction * cone.direction;
        float transmittance = 1.0;
        float along = cone.start;
        [loop] for (uint stepIndex = 0u; stepIndex < MaxConeSteps && along < cone.end; stepIndex++)
        {
            float3 onAxis = cone.origin + cone.direction * along;
            uint finest = GetFinestLevel(onAxis);
            if (finest >= levelCount)
            {
                break;
            }

            float diameter = max(2.0 * along * slope, finestSize);
            float level = clamp(log2(diameter / finestSize), (float)finest, (float)(levelCount - 1u));
            float voxelSize = finestSize * exp2(level);
            float3 position = onAxis + cone.normal * voxelSize;
            uint lower = (uint)level;
            float3 axes = SampleOpacity(lower, position);
            float blend = level - (float)lower;
            [branch] if (blend > 0.0 && lower + 1u < levelCount)
            {
                axes = lerp(axes, SampleOpacity(lower + 1u, position), blend);
            }

            float stepLength = max(diameter, voxelSize) * 0.5;
            float alpha = saturate(dot(axes, weights));
            transmittance *= pow(1.0 - alpha, stepLength / voxelSize);
            if (transmittance < ConeBlocked)
            {
                return 0.0;
            }

            along += stepLength;
        }

        return transmittance;
    }
}

// The key of an instance's look as the voxels see it: its model, the model's first cluster, which a reload moves, and
// its materials, so a change to any of them voxelizes it again.
uint GetLookKey(GpuInstance instance, GpuModel model)
{
    uint key = instance.model * 2654435761u ^ model.firstCluster * 2246822519u;
    key = (key ^ instance.materials.x) * 3266489917u ^ instance.materials.y;
    return (key ^ instance.materials.z) * 668265263u ^ instance.materials.w;
}

// A brick's place among every level's: level by level, each stored wrapped, z then y then x.
uint GetBrickIndex(uint level, int3 worldBrick)
{
    uint3 stored = (uint3)(worldBrick - (int3)VoxelBricks * (int3)floor((float3)worldBrick / (float)VoxelBricks));
    return level * LevelBricks + (stored.z * VoxelBricks + stored.y) * VoxelBricks + stored.x;
}

// A run of bricks of the world, by their place in it: the first and the last along each axis.
struct BrickRange
{
    int3 first;
    int3 last;
}

// The bricks of a level that a sphere of the world reaches, within its window.
BrickRange GetSphereBricks(GpuVoxelLevel row, float4 sphere)
{
    float brickSize = row.voxelSize * (float)VoxelBrick;
    int3 windowFirst = (int3)row.origin / (int)VoxelBrick;
    BrickRange range;
    range.first = max((int3)floor((sphere.xyz - sphere.w) / brickSize), windowFirst);
    range.last = min((int3)floor((sphere.xyz + sphere.w) / brickSize), windowFirst + (int)VoxelBricks - 1);
    return range;
}

bool IsBitSet(ArrayRef<uint> bits, uint index)
{
    return (bits[index >> 5] & (1u << (index & 31u))) != 0u;
}

// Marks every brick a sphere of the world reaches, in every level whose window it reaches.
void MarkSphere(VoxelizeConstants c, float4 sphere)
{
    if (sphere.w <= 0.0)
    {
        return;
    }

    [loop] for (uint level = 0u; level < c.scene.levelCount; level++)
    {
        BrickRange range = GetSphereBricks(c.scene.levels[level], sphere);
        [loop] for (int z = range.first.z; z <= range.last.z; z++)
        {
            [loop] for (int y = range.first.y; y <= range.last.y; y++)
            {
                [loop] for (int x = range.first.x; x <= range.last.x; x++)
                {
                    uint brick = GetBrickIndex(level, int3(x, y, z));
                    c.marked.InterlockedOr(brick >> 5, 1u << (brick & 31u));
                }
            }
        }
    }
}

// One instance slot: what the voxels last saw of it against what it is now, its sphere in the world and its look's
// key; where they differ, the bricks of both spheres are marked and the new kept. An instance not drawn yet, one with
// no clusters, or one that casts no shadow, has no sphere.
void MarkInstance(VoxelizeConstants c, uint slot)
{
    GpuInstance instance = World.instances[slot];
    float4 sphere = float4(0.0, 0.0, 0.0, 0.0);
    uint look = 0u;
    if (instance.maxScale > 0.0 && !instance.isShadowless)
    {
        GpuModel model = World.models[instance.model];
        if (model.clusterCount > 0u)
        {
            sphere = float4(instance.ToWorld(model.bounds.xyz), model.bounds.w * instance.maxScale);
            look = GetLookKey(instance, model);
        }
    }

    float4 seen = c.seenSpheres[slot];
    if (all(seen == sphere) && c.seenLooks[slot] == look)
    {
        return;
    }

    MarkSphere(c, seen);
    MarkSphere(c, sphere);
    c.seenSpheres[slot] = sphere;
    c.seenLooks[slot] = look;
}

// One brick of a level's store: marked when the world brick it holds is not the one it held, as the window moved, or
// when the level was put in place anew.
void MarkEnteringBrick(VoxelizeConstants c, uint brick)
{
    uint level = brick / LevelBricks;
    if (level >= c.scene.levelCount)
    {
        return;
    }

    GpuVoxelLevel row = c.scene.levels[level];
    uint local = brick % LevelBricks;
    int3 stored = int3(local % VoxelBricks, (local / VoxelBricks) % VoxelBricks, local / (VoxelBricks * VoxelBricks));
    int3 first = (int3)row.origin / (int)VoxelBrick;
    int3 lastFirst = (int3)row.lastOrigin / (int)VoxelBrick;
    int3 held = first + ((stored - first) % (int)VoxelBricks + (int)VoxelBricks) % (int)VoxelBricks;
    int3 wasHeld = lastFirst + ((stored - lastFirst) % (int)VoxelBricks + (int)VoxelBricks) % (int)VoxelBricks;
    if ((row.flags & VoxelLevelReset) != 0u || any(held != wasHeld))
    {
        c.marked.InterlockedOr(brick >> 5, 1u << (brick & 31u));
    }
}

// Takes the marked bricks, up to the budget, in the order of their places, so the finest level's first: each lane of
// the last group a run of words, counted, then taken after the lanes before it, each taken brick listed, its bit set in
// taken and cleared from marked. Then the groups ListVoxelJobs runs: a thread an instance slot, then one for each word
// of the taken bricks' bits. Every lane of the group calls it.
void TakeMarkedBricks(VoxelizeConstants c, uint lane)
{
    uint words = c.scene.levelCount * LevelBricks / 32u;
    uint perLane = (words + 63u) / 64u;
    uint first = lane * perLane;
    uint last = min(first + perLane, words);
    uint marked = 0u;
    for (uint word = first; word < last; word++)
    {
        marked += countbits(c.marked.LoadAtomic(word));
    }

    LaneMarked[lane] = marked;
    GroupMemoryBarrierWithGroupSync();
    uint before = 0u;
    uint total = 0u;
    for (uint other = 0u; other < 64u; other++)
    {
        before += other < lane ? LaneMarked[other] : 0u;
        total += LaneMarked[other];
    }

    uint placed = min(before, c.brickBudget);
    for (uint word = first; word < last; word++)
    {
        uint bits = c.marked.LoadAtomic(word);
        uint take = 0u;
        uint left = bits;
        while (left != 0u && placed < c.brickBudget)
        {
            uint bit = firstbitlow(left);
            left &= left - 1u;
            take |= 1u << bit;
            c.takenList[placed] = word * 32u + bit;
            placed++;
        }

        c.taken[word] = take;
        c.marked.StoreAtomic(word, bits & ~take);
    }

    if (lane == 0u)
    {
        uint taken = min(total, c.brickBudget);
        c.counts[CountTaken] = taken;
        c.counts[CountJobs] = 0u;
        c.args[0u] = GetDispatchGroups(World.instances.count + taken * BrickWords, 64u);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void MarkVoxelBricks(
    uint3 group : SV_GroupID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform VoxelizeConstants c)
{
    BeginFramePass(pass);
    uint index = GetLinearGroup(group) * 64u + thread;
    uint slots = World.instances.count;
    if (index < slots)
    {
        MarkInstance(c, index);
    }
    else
    {
        MarkEnteringBrick(c, index - slots);
    }

    if (IsLastGroup(c.counts, CountGroupsDone, thread))
    {
        TakeMarkedBricks(c, thread);
    }
}

// Whether any brick a sphere of the world reaches in a level was taken this frame: a row of bricks along x at a time.
bool IsReachingTaken(VoxelizeConstants c, uint level, float4 sphere)
{
    BrickRange range = GetSphereBricks(c.scene.levels[level], sphere);
    [loop] for (int z = range.first.z; z <= range.last.z; z++)
    {
        [loop] for (int y = range.first.y; y <= range.last.y; y++)
        {
            [loop] for (int x = range.first.x; x <= range.last.x; x++)
            {
                if (IsBitSet(c.taken, GetBrickIndex(level, int3(x, y, z))))
                {
                    return true;
                }
            }
        }
    }

    return false;
}

// One instance slot: for each level its sphere reaches a taken brick of, the clusters of its model's levels that hold
// the cut whose error is half the level's voxel, in jobs of 64. The finest level of the model stands in for any finer.
void ListInstanceJobs(VoxelizeConstants c, uint slot)
{
    float4 sphere = c.seenSpheres[slot];
    if (sphere.w <= 0.0)
    {
        return;
    }

    GpuInstance instance = World.instances[slot];
    GpuModel model = World.models[instance.model];
    [loop] for (uint level = 0u; level < c.scene.levelCount; level++)
    {
        if (!IsReachingTaken(c, level, sphere))
        {
            continue;
        }

        float halfVoxel = c.scene.levels[level].voxelSize * 0.5 / instance.maxScale;
        uint first = 0xFFFFFFFFu;
        uint last = 0u;
        [loop] for (uint modelLevel = 0u; modelLevel < model.levelCount; modelLevel++)
        {
            GpuLevel row = World.levels[model.firstLevel + modelLevel];
            if ((row.minError <= halfVoxel || modelLevel == 0u) && row.maxParentError > halfVoxel)
            {
                first = min(first, row.firstCluster);
                last = max(last, row.firstCluster + row.count);
            }
        }

        if (first >= last)
        {
            continue;
        }

        uint jobs = (last - first + 63u) / 64u;
        uint at = WaveAllocate(c.counts, CountJobs, jobs);
        [loop] for (uint job = 0u; job < jobs && at + job < c.jobs.count; job++)
        {
            uint start = first + job * 64u;
            c.jobs[at + job] = uint4(slot, start, min(64u, last - start), level);
        }
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ListVoxelJobs(
    uint3 group : SV_GroupID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform VoxelizeConstants c)
{
    BeginFramePass(pass);
    uint index = GetLinearGroup(group) * 64u + thread;
    uint slots = World.instances.count;
    if (index < slots)
    {
        ListInstanceJobs(c, index);
    }
    else if ((index - slots) / BrickWords < c.counts[CountTaken])
    {
        uint taken = (index - slots) / BrickWords;
        c.bits[c.takenList[taken] * BrickWords + (index - slots) % BrickWords] = 0u;
    }

    if (IsLastGroup(c.counts, CountGroupsDone, thread) && thread == 0u)
    {
        c.args[1u] = GetDispatchGroups(min(c.counts.LoadAtomic(CountJobs), c.jobs.count), 1u);
        c.args[2u] = GetDispatchGroups(c.counts[CountTaken], 1u);
    }
}

// Sets one half of a voxel, by its place in the level's window in halves, if its brick was taken this frame.
void SetHalf(VoxelizeConstants c, uint level, GpuVoxelLevel row, uint3 place)
{
    int3 voxel = (int3)row.origin + (int3)(place >> 1);
    uint brick = GetBrickIndex(level, voxel >> 3);
    if (!IsBitSet(c.taken, brick))
    {
        return;
    }

    uint3 inBrick = (uint3)(voxel & 7);
    uint byteIndex = brick * BrickWords * 4u + (inBrick.z * VoxelBrick + inBrick.y) * VoxelBrick + inBrick.x;
    uint bit = ((place.z & 1u) * 2u + (place.y & 1u)) * 2u + (place.x & 1u);
    c.bits.InterlockedOr(byteIndex >> 2, (1u << bit) << ((byteIndex & 3u) * 8u));
}

// Whether any brick of a column of a level's window, (u, v) a tile of 16 by 16 halves, from half w0 to w1 along the
// third, was taken this frame.
bool IsColumnTaken(VoxelizeConstants c, uint level, GpuVoxelLevel row, uint3 axes, uint2 tile, uint w0, uint w1)
{
    [loop] for (uint w = w0 / 16u; w <= w1 / 16u; w++)
    {
        uint3 inWindow = uint3(0u, 0u, 0u);
        inWindow[axes.x] = tile.x;
        inWindow[axes.y] = tile.y;
        inWindow[axes.z] = w;
        if (IsBitSet(c.taken, GetBrickIndex(level, (int3)row.origin / (int)VoxelBrick + (int3)inWindow)))
        {
            return true;
        }
    }

    return false;
}

// The least and most depth a plane through a point, at depth at, has over a rectangle, from its four corners, where a
// plane's extremes over it are.
float2 GetPlaneDepths(float at, float2 slopes, float2 through, float2 low, float2 high)
{
    float4 depths = at + float4(
        dot(slopes, low - through),
        dot(slopes, float2(high.x, low.y) - through),
        dot(slopes, float2(low.x, high.y) - through),
        dot(slopes, high - through));
    float2 low2 = min(depths.xy, depths.zw);
    float2 high2 = max(depths.xy, depths.zw);
    return float2(min(low2.x, low2.y), max(high2.x, high2.y));
}

// Sets the halves of a level's voxels a triangle of the world touches. Seen along its dominant axis w, the triangle
// covers a run of halves in each column (u, v) whose square it overlaps, conservatively (an edge's test at the square's
// corner most inside it, Akenine-Möller 2001), from the least to the most depth of its plane over the square, held to
// the triangle's own depths; a tile of 16 by 16 columns, a brick's, whose bricks were none of them taken is skipped.
void VoxelizeTriangle(VoxelizeConstants c, uint level, float3 p0, float3 p1, float3 p2)
{
    GpuVoxelLevel row = c.scene.levels[level];
    float halfSize = row.voxelSize * 0.5;
    float3 corner = row.origin * 2.0;
    float3 a = p0 / halfSize - corner;
    float3 b = p1 / halfSize - corner;
    float3 d = p2 / halfSize - corner;
    float3 normal = cross(b - a, d - a);
    float3 size = abs(normal);
    if (Max3(size) < 1e-8)
    {
        return;
    }

    uint w = size.x >= size.y && size.x >= size.z ? 0u : (size.y >= size.z ? 1u : 2u);
    uint3 axes = uint3((w + 1u) % 3u, (w + 2u) % 3u, w);
    float2 pa = float2(a[axes.x], a[axes.y]);
    float2 pb = float2(b[axes.x], b[axes.y]);
    float2 pd = float2(d[axes.x], d[axes.y]);
    float3 depths = float3(a[w], b[w], d[w]);
    float wide = (float)(VoxelWindow * 2u);
    float minDepth = max(min(min(depths.x, depths.y), depths.z), 0.0);
    float maxDepth = min(Max3(depths), wide - 1e-3);
    float2 low = max(floor(min(min(pa, pb), pd)), 0.0);
    float2 high = min(floor(max(max(pa, pb), pd)), wide - 1.0);
    if (minDepth > maxDepth || any(low > high))
    {
        return;
    }

    // Each edge's line, facing the triangle's inside whichever way it winds, and how far its test reaches past a
    // square's middle toward the square's corner most inside it.
    float orientation = normal[w] >= 0.0 ? 1.0 : -1.0;
    float3x2 edges = float3x2(pb - pa, pd - pb, pa - pd) * orientation;
    float3x2 starts = float3x2(pa, pb, pd);
    float3 reaches = 0.5 * float3(
        abs(edges[0].x) + abs(edges[0].y),
        abs(edges[1].x) + abs(edges[1].y),
        abs(edges[2].x) + abs(edges[2].y));
    float2 slopes = float2(-normal[axes.x], -normal[axes.y]) / normal[w];

    uint2 firstTile = (uint2)low / 16u;
    uint2 lastTile = (uint2)high / 16u;
    [loop] for (uint tv = firstTile.y; tv <= lastTile.y; tv++)
    {
        [loop] for (uint tu = firstTile.x; tu <= lastTile.x; tu++)
        {
            float2 tileLow = max(float2(tu, tv) * 16.0, low);
            float2 tileHigh = min(float2(tu, tv) * 16.0 + 16.0, high + 1.0);
            float2 tileDepths = GetPlaneDepths(a[w], slopes, pa, tileLow, tileHigh);
            uint w0 = (uint)clamp(tileDepths.x, minDepth, maxDepth);
            uint w1 = (uint)clamp(tileDepths.y, minDepth, maxDepth);
            if (!IsColumnTaken(c, level, row, axes, uint2(tu, tv), w0, w1))
            {
                continue;
            }

            [loop] for (uint cv = (uint)tileLow.y; cv < (uint)tileHigh.y; cv++)
            {
                [loop] for (uint cu = (uint)tileLow.x; cu < (uint)tileHigh.x; cu++)
                {
                    float2 middle = float2(cu, cv) + 0.5;
                    bool isInside = true;
                    [unroll] for (uint e = 0u; e < 3u; e++)
                    {
                        float2 offset = middle - starts[e];
                        float side = edges[e].x * offset.y - edges[e].y * offset.x;
                        isInside = isInside && side + reaches[e] >= 0.0;
                    }

                    if (!isInside)
                    {
                        continue;
                    }

                    float2 columnDepths = GetPlaneDepths(a[w], slopes, pa, float2(cu, cv), float2(cu, cv) + 1.0);
                    uint s0 = (uint)clamp(columnDepths.x, minDepth, maxDepth);
                    uint s1 = (uint)clamp(columnDepths.y, minDepth, maxDepth);
                    [loop] for (uint s = s0; s <= s1; s++)
                    {
                        uint3 place = uint3(0u, 0u, 0u);
                        place[axes.x] = cu;
                        place[axes.y] = cv;
                        place[axes.z] = s;
                        SetHalf(c, level, row, place);
                    }
                }
            }
        }
    }
}

// One cluster of a job's run: if it is on the cut for the job's level, its error no more than half the level's voxel
// (or its model's finest) and its parent's more, and its material is drawn opaque, each of its triangles is voxelized.
[shader("compute")]
[numthreads(64, 1, 1)]
void VoxelizeClusters(
    uint3 group : SV_GroupID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform VoxelizeConstants c)
{
    BeginFramePass(pass);
    uint index = GetLinearGroup(group);
    if (index >= min(c.counts[CountJobs], c.jobs.count))
    {
        return;
    }

    uint4 job = c.jobs[index];
    if (thread >= job.z)
    {
        return;
    }

    GpuInstance instance = World.instances[job.x];
    GpuCluster cluster = World.clusters[job.y + thread];
    float halfVoxel = c.scene.levels[job.w].voxelSize * 0.5 / instance.maxScale;
    bool isOnCut = (cluster.error <= halfVoxel || cluster.level == 0u) && cluster.parentError > halfVoxel;
    uint material = instance.GetMaterial(cluster.slot);
    if (!isOnCut || material == 0xFFFFu || !IsDrawnOpaque(World.materialBins[material]))
    {
        return;
    }

    [loop] for (uint face = 0u; face < cluster.triangleCount; face++)
    {
        uint3 corners = cluster.LoadTriangle(face);
        VoxelizeTriangle(
            c,
            job.w,
            instance.ToWorld(cluster.LoadPosition(corners.x)),
            instance.ToWorld(cluster.LoadPosition(corners.y)),
            instance.ToWorld(cluster.LoadPosition(corners.z)));
    }
}

// A voxel's opacity along each axis from its eight halves: the share of the four columns of halves along the axis
// that hold one.
float3 GetDirectionalOpacity(uint halves)
{
    float3 columns = float3(0.0, 0.0, 0.0);
    [unroll] for (uint i = 0u; i < 2u; i++)
    {
        [unroll] for (uint j = 0u; j < 2u; j++)
        {
            // Bit (z * 2 + y) * 2 + x: along x the pair at (i, j) in y and z, along y in x and z, along z in x and y.
            uint alongX = (halves >> ((j * 2u + i) * 2u)) & 3u;
            uint alongY = ((halves >> ((j * 2u) * 2u + i)) | (halves >> ((j * 2u + 1u) * 2u + i))) & 1u;
            uint alongZ = ((halves >> ((j * 2u) + i)) | (halves >> ((2u + j) * 2u + i))) & 1u;
            columns += float3(alongX != 0u ? 1.0 : 0.0, (float)alongY, (float)alongZ);
        }
    }

    return columns * 0.25;
}

// A voxel of a taken brick: its opacity written at its place in its level's slices, and into the padding as well when
// it is at either end of the level along z.
[shader("compute")]
[numthreads(8, 8, 8)]
void ResolveVoxelOpacity(
    uint3 group : SV_GroupID,
    uint3 inBrick : SV_GroupThreadID,
    uniform PassHeader pass,
    uniform VoxelizeConstants c)
{
    BeginFramePass(pass);
    uint index = GetLinearGroup(group);
    if (index >= c.counts[CountTaken])
    {
        return;
    }

    uint brick = c.takenList[index];
    uint level = brick / LevelBricks;
    uint local = brick % LevelBricks;
    uint3 stored = uint3(local % VoxelBricks, (local / VoxelBricks) % VoxelBricks, local / (VoxelBricks * VoxelBricks));
    uint3 voxel = stored * VoxelBrick + inBrick;
    uint byteIndex = brick * BrickWords * 4u + (inBrick.z * VoxelBrick + inBrick.y) * VoxelBrick + inBrick.x;
    uint halves = (c.bits[byteIndex >> 2] >> ((byteIndex & 3u) * 8u)) & 0xFFu;
    float4 opacity = float4(GetDirectionalOpacity(halves), 0.0);
    VolumeLevelRef target = c.scene.opacity.GetLevel(0u);
    uint base = level * LevelSlices;
    target[uint3(voxel.xy, base + 1u + voxel.z)] = opacity;
    if (voxel.z == 0u)
    {
        target[uint3(voxel.xy, base + LevelSlices - 1u)] = opacity;
    }

    if (voxel.z == VoxelWindow - 1u)
    {
        target[uint3(voxel.xy, base)] = opacity;
    }
}
