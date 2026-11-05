// The global distance field kept up to date: a model's field written into the atlas as it arrives; each frame the
// clipmaps' bricks that slid out of their window, or that an instance moved through, marked dirty; the instances
// over each dirty brick listed; and every dirty brick's voxels made again from those instances' fields. The pass
// header's x is the frame's constants and w the field constants.
module "Field";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Instances;
import Scene.Field;
import Shadow.Virtual;
import Trace.DistanceField;

// A model's field, as bytes in the buffer the header's y names, into its slot of the atlas, the header's z.
[shader("compute")]
[numthreads(8, 8, 8)]
void FieldWriteModel(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread >= FieldModelVoxels))
        return;

    FieldConstants field = GetFieldConstants();
    PassHeader header = GetPassHeader();
    BufferRef bytes = { header.y };
    uint index = (thread.z * FieldModelVoxels + thread.y) * FieldModelVoxels + thread.x;
    uint word = bytes.Load<uint>((index / 4u) * 4u);
    uint byte = (word >> ((index % 4u) * 8u)) & 0xFFu;
    int signed = byte >= 128u ? int(byte) - 256 : int(byte);
    StorageVolumeRef atlas = { field.atlas, 0u };
    atlas.Store(GetModelFieldOrigin(header.z) + thread, float4(float(signed) / 127.0, 0.0, 0.0, 0.0));
}

// Each brick whose cell holds another brick now that the window moved takes the new one and waits to be made.
[shader("compute")]
[numthreads(64, 1, 1)]
void FieldScroll(uint3 thread : SV_DispatchThreadID)
{
    FieldConstants field = GetFieldConstants();
    uint cell = thread.x;
    if (cell >= FieldLevels * FieldBricksPerLevel)
        return;

    uint level = cell / FieldBricksPerLevel;
    uint local = cell % FieldBricksPerLevel;
    int3 localBrick = int3(local % FieldBricks, (local / FieldBricks) % FieldBricks, local / (FieldBricks * FieldBricks));
    int3 origin = GetFieldWindowBrick(field, level);
    int3 world = origin + ((localBrick - origin) & int(FieldBricks - 1u));
    ArrayRef<FieldBrick> bricks = { field.bricks };
    FieldBrick brick = bricks[cell];
    if (all(brick.world == world) && (brick.flags & 0x80000000u) != 0u)
        return;

    brick.world = world;
    brick.flags = FieldBrickDirty | 0x80000000u;
    bricks[cell] = brick;
}

// Each instance that changed marks the bricks under where it was and where it is.
[shader("compute")]
[numthreads(64, 1, 1)]
void FieldInvalidate(uint3 thread : SV_DispatchThreadID)
{
    FieldConstants field = GetFieldConstants();
    if (thread.x >= field.dirtyInstanceCount)
        return;

    ArrayRef<DirtyInstance> dirty = { field.dirtyInstances };
    DirtyInstance instance = dirty[thread.x];
    MarkSphere(field, instance.oldCenter, instance.oldRadius);
    MarkSphere(field, instance.newCenter, instance.newRadius);
}

void MarkSphere(FieldConstants field, float3 center, float radius)
{
    if (radius <= 0.0)
        return;

    BufferRef bricks = { field.bricks };
    for (uint level = 0u; level < FieldLevels; level++)
    {
        int3 first;
        int3 last;
        if (!GetBrickRange(field, level, center, radius, first, last))
            continue;

        for (int z = first.z; z <= last.z; z++)
        {
            for (int y = first.y; y <= last.y; y++)
            {
                for (int x = first.x; x <= last.x; x++)
                {
                    bricks.InterlockedOr((GetFieldBrickCell(level, int3(x, y, z)) * 16u) + 12u, FieldBrickDirty);
                }
            }
        }
    }
}

// The bricks of a level's window a sphere, widened by the band, touches; false when none.
bool GetBrickRange(FieldConstants field, uint level, float3 center, float radius, out int3 first, out int3 last)
{
    float voxelSize = field.voxelSizes[level];
    float reach = radius + FieldBandVoxels * voxelSize;
    int3 origin = GetFieldWindowBrick(field, level);
    int3 end = origin + int(FieldBricks) - 1;
    first = max(GetFieldBrick(int3(floor((center - reach) / voxelSize))), origin);
    last = min(GetFieldBrick(int3(floor((center + reach) / voxelSize))), end);
    return all(first <= last);
}

// Each dirty brick joins the list to be made, with no instances yet.
[shader("compute")]
[numthreads(64, 1, 1)]
void FieldCollect(uint3 thread : SV_DispatchThreadID)
{
    FieldConstants field = GetFieldConstants();
    uint cell = thread.x;
    if (cell >= FieldLevels * FieldBricksPerLevel)
        return;

    ArrayRef<FieldBrick> bricks = { field.bricks };
    if ((bricks[cell].flags & FieldBrickDirty) == 0u)
        return;

    BufferRef counts = { field.counts };
    uint slot = counts.InterlockedAdd(FieldCountDirtyBricks, 1u);
    if (slot >= field.maxDirtyBricks)
        return;

    ArrayRef<uint> dirtyBricks = { field.dirtyBricks };
    dirtyBricks[slot] = cell;
    ArrayRef<uint> lists = { field.lists };
    lists[cell * (FieldInstancesPerBrick + 1u)] = 0u;
    BufferRef arguments = { field.dirtyArguments };
    arguments.InterlockedMax(0u, slot + 1u);
}

// Each instance with a field joins the list of every dirty brick under it. The groups go along x up to
// FieldListGroupsPerRow and then down y, as a GPU takes no more along one axis.
[shader("compute")]
[numthreads(64, 1, 1)]
void FieldList(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    FieldConstants field = GetFieldConstants();
    uint instanceIndex = (((group.y * FieldListGroupsPerRow) + group.x) * 64u) + local.x;
    if (instanceIndex >= field.instanceCount)
        return;

    GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
    if (instance.clusterCount == 0u || instance.flags == 0u)
        return;

    ArrayRef<FieldBrick> bricks = { field.bricks };
    BufferRef lists = { field.lists };
    for (uint level = 0u; level < FieldLevels; level++)
    {
        int3 first;
        int3 last;
        if (!GetBrickRange(field, level, instance.boundsCenter, instance.boundsRadius, first, last))
            continue;

        for (int z = first.z; z <= last.z; z++)
        {
            for (int y = first.y; y <= last.y; y++)
            {
                for (int x = first.x; x <= last.x; x++)
                {
                    uint cell = GetFieldBrickCell(level, int3(x, y, z));
                    if ((bricks[cell].flags & FieldBrickDirty) == 0u)
                        continue;

                    uint count = lists.InterlockedAdd(cell * (FieldInstancesPerBrick + 1u) * 4u, 1u);
                    if (count < FieldInstancesPerBrick)
                        lists.Store<uint>((cell * (FieldInstancesPerBrick + 1u) + 1u + count) * 4u, instanceIndex);
                }
            }
        }
    }
}

// Each dirty brick's voxels: the least distance over the fields of the instances listed for it, in the level's band.
// A model's distance scales by the least its instance scales an axis, so a stretched instance never reads farther
// than it is.
[shader("compute")]
[numthreads(8, 8, 8)]
void FieldUpdate(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    FieldConstants field = GetFieldConstants();
    BufferRef counts = { field.counts };
    if (group.x >= min(counts.Load<uint>(FieldCountDirtyBricks), field.maxDirtyBricks))
        return;

    ArrayRef<uint> dirtyBricks = { field.dirtyBricks };
    uint cell = dirtyBricks[group.x];
    uint level = cell / FieldBricksPerLevel;
    ArrayRef<FieldBrick> bricks = { field.bricks };
    FieldBrick brick = bricks[cell];
    float voxelSize = field.voxelSizes[level];
    float band = FieldBandVoxels * voxelSize;
    int3 voxel = (brick.world * int(FieldBrickVoxels)) + int3(local);
    float3 world = (float3(voxel) + 0.5) * voxelSize;

    ArrayRef<uint> lists = { field.lists };
    ArrayRef<GpuModelField> modelFields = { field.modelFields };
    VolumeRef atlas = { field.atlas };
    SamplerRef sampler = { field.sampler };
    uint count = min(lists[cell * (FieldInstancesPerBrick + 1u)], FieldInstancesPerBrick);
    float distance = band;
    for (uint i = 0u; i < count; i++)
    {
        GpuInstance instance = LoadInstance(frame.instances, lists[(cell * (FieldInstancesPerBrick + 1u)) + 1u + i]);
        GpuModelField modelField = modelFields[instance.flags - 1u];
        float3 model = mul(float4(world, 1.0), instance.inverseWorld).xyz;
        float scale = GetMinScale(instance.world);
        float3 extent = float(FieldModelVoxels - 1u) * modelField.voxel;
        float3 inField = clamp(model - modelField.origin, float3(0.0), extent);
        float3 outside = (model - modelField.origin) - inField;
        float3 texel = float3(GetModelFieldOrigin(instance.flags - 1u)) + inField / modelField.voxel + 0.5;
        float sampled = atlas.SampleLevel(sampler, texel / float(FieldAtlasVoxels), 0.0).x * modelField.band;
        distance = min(distance, (max(sampled, 0.0) + length(outside)) * scale);
        if (all(outside == float3(0.0)))
            distance = min(distance, sampled * scale);
    }

    StorageVolumeRef clipmap = { GetClipmap(field, level), 0u };
    clipmap.Store(GetFieldTexel(voxel), float4(clamp(distance / band, -1.0, 1.0), 0.0, 0.0, 0.0));
    if (all(local == uint3(0u)))
    {
        brick.flags &= ~FieldBrickDirty;
        bricks[cell] = brick;
    }
}
