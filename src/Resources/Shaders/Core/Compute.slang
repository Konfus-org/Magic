// What compute passes share: work a wave of lanes does together (Vulkan's subgroup operations), each call made where
// every lane taking part is active and giving the same result whatever the wave's size, 32 or 64 by GPU; the last group
// of a dispatch, which finishes what every group's work adds up to, so no pass of one group is needed after it; and
// what an indirect dispatch or draw reads from a buffer, as a pass writes it for one after it: a dispatch's groups are
// three uints (GpuComputePass.DispatchFromBuffer), an indexed draw's arguments IndexedDrawArgs
// (GpuRenderPass.DrawIndexedFromBuffer).

module "Core/Compute";

import Core.Heap;

public struct IndexedDrawArgs
{
    uint indices;
    uint instances;
    uint firstIndex;
    int vertexOffset;
    uint firstInstance;
}

// Groups an indirect dispatch runs along x at most, as every API allows; more go on in y.
public static const uint MaxGroupsX = 65535u;

// A group's number among all of an indirect dispatch's, which run MaxGroupsX to a row.
public uint GetLinearGroup(uint3 group)
{
    return group.y * MaxGroupsX + group.x;
}

// The groups that take work, perGroup to a group, MaxGroupsX to a row: an indirect dispatch's arguments. One group at
// least, so a pass whose last group finishes something always has one.
public uint3 GetDispatchGroups(uint work, uint perGroup)
{
    uint groups = max((work + perGroup - 1u) / max(perGroup, 1u), 1u);
    return uint3(min(groups, MaxGroupsX), (groups + MaxGroupsX - 1u) / MaxGroupsX, 1u);
}

groupshared bool LastGroup;

// Whether this group is the last of its dispatch to get here, every other having finished: the one that then reads,
// through LoadAtomic, what all of them wrote through atomics. The groups count themselves at counters[index], which
// the last puts back to 0 for the next dispatch. Every lane of the group calls it, none having returned before.
public bool IsLastGroup(ArrayRef<uint> counters, uint index, uint thread)
{
    DeviceMemoryBarrierWithGroupSync();
    if (thread == 0u)
    {
        uint3 groups = WorkgroupCount();
        bool isLast = counters.InterlockedAdd(index, 1u) + 1u == groups.x * groups.y * groups.z;
        if (isLast)
        {
            counters[index] = 0u;
        }

        LastGroup = isLast;
    }

    GroupMemoryBarrierWithGroupSync();
    return LastGroup;
}

// The first of the count places a lane takes in a count a buffer keeps at index: the wave's places taken
// together by one atomic (Adinets 2014, "Optimized Filtering with Warp-Aggregated Atomics"), each lane's after the
// lanes' before it, as the places were as arbitrary as the atomics' order already. Only the lanes active where it is
// called take places.
public uint WaveAllocate(ArrayRef<uint> counts, uint index, uint count)
{
    uint before = WavePrefixSum(count);
    uint total = WaveActiveSum(count);
    uint first = 0u;
    if (WaveIsFirstLane())
    {
        first = counts.InterlockedAdd(index, total);
    }

    return WaveReadLaneFirst(first) + before;
}
