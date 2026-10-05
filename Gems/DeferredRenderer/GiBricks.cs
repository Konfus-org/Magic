using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// The occupancy bricks of the meshes in use: <see cref="BrickSize"/> voxels a side of the mesh's own box, one per
/// mesh slot in the brick atlas (<see cref="RenderContext.BrickAtlas"/>), reference counted by the draw groups that use
/// the mesh, built on the GPU from the mesh's triangles by the brick passes the frame after they are asked for
/// (<see cref="Pending"/>, handed over as <see cref="Jobs"/>). The atlas has <see cref="BrickCount"/>; a mesh asked for
/// past that gets <see cref="None"/> and is stamped by its bounds alone.
/// </summary>
internal sealed class GiBricks : RefCountTable<uint, uint>
{
    public const uint None = uint.MaxValue;

    public const int BrickSize = 16;

    public const int BricksAcross = 16;

    public const int BricksDeep = 8;

    public const int BrickCount = BricksAcross * BricksAcross * BricksDeep;

    private readonly Stack<uint> _free = new();

    public GiBricks()
    {
        for (uint brick = BrickCount; brick > 0; brick--)
            _free.Push(brick - 1);
    }

    /// <summary>
    /// The mesh slots whose bricks are still to be built, in the order they were asked for.
    /// </summary>
    public Queue<uint> Pending { get; } = [];

    public int Dropped { get; private set; }

    /// <summary>
    /// A mesh box with a little thickness on any flat axis, so a plane rasterises into a slab of its brick and stamps
    /// as a thin wall rather than nothing.
    /// </summary>
    public static Aabb InflateFlat(in Aabb box)
    {
        Vector3 extent = box.Max - box.Min;
        float longest = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        float least = MathF.Max(longest * 0.02f, 1e-3f);
        Vector3 pad = new(extent.X < least ? (least - extent.X) * 0.5f : 0f, extent.Y < least ? (least - extent.Y) * 0.5f : 0f, extent.Z < least ? (least - extent.Z) * 0.5f : 0f);
        return new Aabb(box.Min - pad, box.Max + pad);
    }

    /// <summary>
    /// The brick of a mesh slot, taking a reference; a new one is queued to build.
    /// </summary>
    public uint Acquire(uint meshSlot)
    {
        if (TryAcquire(meshSlot, out uint brick))
            return brick;

        if (_free.Count == 0)
        {
            Dropped++;
            Add(meshSlot, None);
            return None;
        }

        brick = _free.Pop();
        Add(meshSlot, brick);
        Pending.Enqueue(meshSlot);
        return brick;
    }

    /// <summary>
    /// Drops a reference; the last gives the brick back.
    /// </summary>
    public void ReleaseMesh(uint meshSlot)
    {
        if (Release(meshSlot, out uint brick) && brick != None)
            _free.Push(brick);
    }

    /// <summary>
    /// The brick a mesh slot holds now, or <see cref="None"/>.
    /// </summary>
    public uint BrickOf(uint meshSlot)
    {
        return TryGet(meshSlot, out uint brick) ? brick : None;
    }

    /// <summary>
    /// Up to <paramref name="budget"/> of the pending bricks as jobs, taken off the queue (a mesh released before its
    /// brick was built is skipped), each with its indirect dispatch: one group of 64 threads per triangle run.
    /// </summary>
    public int Jobs(MeshTable meshes, int budget, Span<GpuBrickJob> jobs, Span<UintVector4> dispatches)
    {
        int count = 0;
        while (count < budget && Pending.TryDequeue(out uint meshSlot))
        {
            uint brick = BrickOf(meshSlot);
            if (brick == None)
                continue;

            (uint firstIndex, uint indexCount, int vertexOffset) = meshes.Range(meshSlot);
            Aabb box = InflateFlat(meshes.Box(meshSlot));
            jobs[count] = new GpuBrickJob { BoxMin = new Vector4(box.Min, 0f), BoxMax = new Vector4(box.Max, 0f), FirstIndex = firstIndex, IndexCount = indexCount, VertexOffset = vertexOffset, Brick = brick };
            dispatches[count] = new UintVector4(((indexCount / 3) + 63) / 64, 1, 1, 0);
            count++;
        }

        return count;
    }
}
