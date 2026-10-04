using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// The occupancy bricks of the meshes in use: <see cref="GiVolumes.BrickSize"/> voxels a side of the mesh's own box,
/// one per mesh slot, reference counted by the draw groups that use the mesh, built on the GPU from the mesh's
/// triangles the frame after they are asked for (<see cref="Pending"/>). The atlas has <see cref="GiVolumes.BrickCount"/>;
/// a mesh asked for past that gets <see cref="None"/> and is stamped by its bounds alone.
/// </summary>
internal sealed class GiBricks : RefCountTable<uint, uint>
{
    public const uint None = uint.MaxValue;

    private readonly Stack<uint> _free = new();

    public GiBricks()
    {
        for (uint brick = GiVolumes.BrickCount; brick > 0; brick--)
            _free.Push(brick - 1);
    }

    /// <summary>
    /// The mesh slots whose bricks are still to be built, in the order they were asked for.
    /// </summary>
    public Queue<uint> Pending { get; } = [];

    public int Dropped { get; private set; }

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
}
