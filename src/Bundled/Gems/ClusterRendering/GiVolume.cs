using System.Numerics;
using System.Runtime.CompilerServices;

namespace Magic.ClusterRendering;

/// <summary>
/// Where the world's field for global illumination lies this frame and what of it is made again: nested levels around
/// the main camera, each <see cref="Resolution"/> voxels a side and twice the last one's voxel, stored wrapping around
/// so a level that moves with the camera keeps what it already holds and makes only the slabs it moved into, and around
/// what moved (<see cref="Instances.Moved"/>) or took another colour (<see cref="Instances.Recoloured"/>) the bricks
/// it touched. Each level has a grid of <see cref="Probes"/>
/// probes a side, a share of them traced each frame: all of the finest level's every
/// <see cref="ClusterRenderingSettings.GiUpdateEvery"/> frames, and of each coarser level, whose probes light what is
/// further and larger, half as often. A probe blends what it was traced with into what it held by how long it waited,
/// so light everywhere follows a change in about the same time whatever the frame rate and however seldom a level is
/// traced (<see cref="Response"/>). While a domain is readied out of sight, every probe is traced every frame and holds
/// the plain mean of its tracings, for <see cref="SettleTracings"/> frames, so the world is shown lit as it settles to
/// rather than gathering its light over seconds; and a new world forgets the old one's light first, so none of it
/// glows on in the new one. Render thread.
/// </summary>
internal sealed class GiVolume
{
    /// <summary>
    /// Voxels a side of a level; the shaders' GiResolution.
    /// </summary>
    internal const int Resolution = 64;

    /// <summary>
    /// Tracings each probe takes, one a frame, while a domain is readied: enough for their mean to hold a few rays'
    /// noise and for light to have bounced as many times, each tracing seeing what the last held.
    /// </summary>
    internal const int SettleTracings = 16;

    /// <summary>
    /// Voxels a side of a brick, what is made again at once; a level moves a brick at a time.
    /// </summary>
    internal const int Brick = 8;

    /// <summary>
    /// The most levels, which the volumes the passes make hold.
    /// </summary>
    internal const int MaxLevels = 5;

    /// <summary>
    /// Probes a side of a level; the shaders' GiProbes.
    /// </summary>
    private const int Probes = 16;

    /// <summary>
    /// Voxels a distance reaches past a surface, beyond which the field says no more: what a voxel near something that
    /// moved is made again within.
    /// </summary>
    private const int Reach = 4;

    private const int Bricks = Resolution / Brick;

    /// <summary>
    /// Seconds the finest level's probes take to come most of the way (all but 1/e) to a change of light; each coarser
    /// level, traced half as often, takes √2 times as long, so the fewer tracings it blends are no noisier.
    /// </summary>
    private const float Response = 0.3f;

    /// <summary>
    /// The least and most of what a probe held it keeps as it takes in a tracing: never so little that one tracing's
    /// noise shows, nor so much that it never moves.
    /// </summary>
    private const float LeastKept = 0.5f;
    private const float MostKept = 0.98f;

    private readonly Level[] _levels = new Level[MaxLevels];

    /// <summary>
    /// <c>GiLevels</c>: a <see cref="GpuGi"/>, then a <see cref="GpuGiLevel"/> for each level.
    /// </summary>
    private readonly GpuTable<GpuGiLevel> _rows;
    private uint _frame;

    internal GiVolume(GpuResources resources)
    {
        _rows = resources.MakeTable<GpuGiLevel>(GpuContract.GiLevels, 1 + MaxLevels);
        for (int level = 0; level < MaxLevels; level++)
        {
            _levels[level] = new Level();
        }
    }

    /// <summary>
    /// Rays traced this frame, every probe due's.
    /// </summary>
    internal uint Rays { get; private set; }

    /// <summary>
    /// Threads the blend of this frame's probes runs, 256 a probe.
    /// </summary>
    internal uint ProbeThreads { get; private set; }

    /// <summary>
    /// Nothing is held: the next frame makes every level whole.
    /// </summary>
    internal void Clear()
    {
        foreach (Level level in _levels)
        {
            level.IsHeld = false;
        }

        Rays = 0;
        ProbeThreads = 0;
    }

    /// <summary>
    /// Places the levels around <paramref name="camera"/>, marks what is made again, from where they moved, from
    /// <paramref name="moved"/> and from <paramref name="recoloured"/>, and uploads them with which probes are traced
    /// this frame and how much of what they held they keep, from the frame's <paramref name="seconds"/>; or, while a
    /// domain is readied, its <paramref name="settling"/>-th frame drawn, every probe, keeping that frame's share of the
    /// mean of its tracings. When a new world <paramref name="forgets"/> the old one, every level is made again whole
    /// and every probe lets go of what it held, so nothing of the old world's light is carried into it.
    /// </summary>
    internal void Plan(
        Vector3 camera,
        IReadOnlyList<MovedSphere> moved,
        IReadOnlyList<MovedSphere> recoloured,
        float seconds,
        int? settling,
        bool forgets,
        ClusterRenderingSettings settings)
    {
        int levels = Math.Clamp(settings.GiLevels, 1, MaxLevels);
        float finest = Math.Clamp(settings.GiVoxel, 0.05f, 4);
        uint every = BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(settings.GiUpdateEvery, 1, 64));
        uint rays = (uint)Math.Clamp(settings.GiRays, 8, 256);

        // While a domain is readied, the n-th frame's tracing keeps n of n + 1 shares, the mean of every one so far, up
        // to what the level keeps at its own pace.
        bool isSettling = settling < SettleTracings;
        float settled = isSettling ? settling.GetValueOrDefault() / (settling.GetValueOrDefault() + 1f) : 1;
        for (int index = 0; index < levels; index++)
        {
            Level level = _levels[index];
            float voxel = finest * (1 << index);
            level.Clear();
            (int X, int Y, int Z) origin = (Snap(camera.X, voxel), Snap(camera.Y, voxel), Snap(camera.Z, voxel));
            if (forgets || !level.IsHeld || level.Voxel != voxel)
            {
                level.MarkAll(origin);
            }
            else
            {
                level.MarkMoved(origin);
            }

            level.Voxel = voxel;
            uint period = every << index;
            level.Period = isSettling ? 1 : period;
            level.Hysteresis = MathF.Min(
                settled,
                Math.Clamp(
                    MathF.Exp(-period * seconds / (Response * MathF.Pow(MathF.Sqrt(2), index))),
                    LeastKept,
                    MostKept));
            level.IsHeld = true;
            Mark(level, moved, voxel);
            Mark(level, recoloured, voxel);
        }

        for (int index = levels; index < MaxLevels; index++)
        {
            _levels[index].IsHeld = false;
        }

        GpuGi header = new()
        {
            Levels = (uint)levels,
            Rays = rays,
            Seed = _frame,
            Steadies = isSettling ? 0u : 1u,
            Camera = camera,
            Forgets = forgets ? 1u : 0u,
        };
        _rows.Set(0, Unsafe.As<GpuGi, GpuGiLevel>(ref header));
        for (int index = 0; index < MaxLevels; index++)
        {
            _rows.Set(1 + index, _levels[index].GetRow());
        }

        uint due = 0;
        for (int index = 0; index < levels; index++)
        {
            due += ((Probes * Probes * Probes) + _levels[index].Period - 1) / _levels[index].Period;
        }

        Rays = due * rays;
        ProbeThreads = due * 256;
        _frame++;
    }

    /// <summary>
    /// The first voxel of a level around <paramref name="metres"/> along one axis, a whole brick, with the camera in
    /// its middle brick.
    /// </summary>
    private static int Snap(double metres, float voxel)
        => ((int)Math.Floor(metres / voxel / Brick) * Brick) - (Resolution / 2);

    /// <summary>
    /// Marks what <paramref name="spheres"/> reach of <paramref name="level"/>, of voxels <paramref name="voxel"/>
    /// metres a side, to be made again, with as far past them as a distance reaches (<see cref="Reach"/>).
    /// </summary>
    private static void Mark(Level level, IReadOnlyList<MovedSphere> spheres, float voxel)
    {
        foreach (MovedSphere sphere in spheres)
        {
            Vector3 centre = sphere.Centre;
            float reach = sphere.Radius + (Reach * voxel);
            level.Mark(centre - new Vector3(reach), centre + new Vector3(reach));
        }
    }

    /// <summary>
    /// One level: where it is, whether what it holds is whole, and the bricks made again this frame.
    /// </summary>
    private sealed class Level
    {
        private (int X, int Y, int Z) _origin;
        private (int X, int Y, int Z) _low;
        private (int X, int Y, int Z) _high;
        private BrickMask _dirty;

        internal bool IsHeld { get; set; }

        internal float Voxel { get; set; } = 1;

        /// <summary>
        /// Frames a probe of it waits to be traced again.
        /// </summary>
        internal uint Period { get; set; } = 1;

        /// <summary>
        /// How much of what a probe of it held it keeps as it takes in what it was traced with.
        /// </summary>
        internal float Hysteresis { get; set; }

        /// <summary>
        /// The level's first brick and its last, by their place in bricks from the world's origin.
        /// </summary>
        private (int X, int Y, int Z) FirstBrick => (_origin.X / Brick, _origin.Y / Brick, _origin.Z / Brick);

        private (int X, int Y, int Z) LastBrick
            => (FirstBrick.X + Bricks - 1, FirstBrick.Y + Bricks - 1, FirstBrick.Z + Bricks - 1);

        internal void Clear()
        {
            _dirty = default;
            _low = (int.MaxValue, int.MaxValue, int.MaxValue);
            _high = (int.MinValue, int.MinValue, int.MinValue);
        }

        /// <summary>
        /// Moves to <paramref name="origin"/> and makes every brick again.
        /// </summary>
        internal void MarkAll((int X, int Y, int Z) origin)
        {
            _origin = origin;
            MarkBricks(FirstBrick, LastBrick);
        }

        /// <summary>
        /// Moves to <paramref name="origin"/>, making again the bricks it moved into.
        /// </summary>
        internal void MarkMoved((int X, int Y, int Z) origin)
        {
            (int X, int Y, int Z) was = _origin;
            _origin = origin;
            (int X, int Y, int Z) first = FirstBrick;
            for (int z = 0; z < Bricks; z++)
            {
                for (int y = 0; y < Bricks; y++)
                {
                    for (int x = 0; x < Bricks; x++)
                    {
                        (int X, int Y, int Z) brick = (first.X + x, first.Y + y, first.Z + z);
                        if (!WasIn(brick, was)) MarkBrick(brick.X, brick.Y, brick.Z);
                    }
                }
            }
        }

        /// <summary>
        /// Makes again the bricks of this level that a box, in metres, touches.
        /// </summary>
        internal void Mark(Vector3 min, Vector3 max)
        {
            (int X, int Y, int Z) first = FirstBrick;
            (int X, int Y, int Z) last = LastBrick;
            (int X, int Y, int Z) low = (
                Math.Max(FloorToBrick(min.X), first.X),
                Math.Max(FloorToBrick(min.Y), first.Y),
                Math.Max(FloorToBrick(min.Z), first.Z));
            (int X, int Y, int Z) high = (
                Math.Min(FloorToBrick(max.X), last.X),
                Math.Min(FloorToBrick(max.Y), last.Y),
                Math.Min(FloorToBrick(max.Z), last.Z));
            MarkBricks(low, high);
        }

        internal GpuGiLevel GetRow()
        {
            bool isDirty = _low.X <= _high.X;
            return new GpuGiLevel
            {
                OriginX = _origin.X,
                OriginY = _origin.Y,
                OriginZ = _origin.Z,
                Voxel = IsHeld ? Voxel : 0,
                DirtyMinX = isDirty ? _low.X * Brick : 0,
                DirtyMinY = isDirty ? _low.Y * Brick : 0,
                DirtyMinZ = isDirty ? _low.Z * Brick : 0,
                DirtyMaxX = isDirty ? ((_high.X + 1) * Brick) - 1 : -1,
                DirtyMaxY = isDirty ? ((_high.Y + 1) * Brick) - 1 : -1,
                DirtyMaxZ = isDirty ? ((_high.Z + 1) * Brick) - 1 : -1,
                Period = Period,
                Hysteresis = Hysteresis,
                Dirty = _dirty,
            };
        }

        /// <summary>
        /// Whether the brick was within the level when its origin was <paramref name="was"/>: one that was not is new
        /// to the level and has to be traced.
        /// </summary>
        private static bool WasIn((int X, int Y, int Z) brick, (int X, int Y, int Z) was)
            => IsIn(brick.X * Brick, was.X) && IsIn(brick.Y * Brick, was.Y) && IsIn(brick.Z * Brick, was.Z);

        private static bool IsIn(int voxel, int origin) => voxel >= origin && voxel < origin + Resolution;

        private static int Wrap(int brick) => ((brick % Bricks) + Bricks) % Bricks;

        /// <summary>
        /// The brick a place in metres is in, along one axis.
        /// </summary>
        private int FloorToBrick(float metres) => (int)MathF.Floor(metres / Voxel / Brick);

        private void MarkBricks((int X, int Y, int Z) low, (int X, int Y, int Z) high)
        {
            for (int z = low.Z; z <= high.Z; z++)
            {
                for (int y = low.Y; y <= high.Y; y++)
                {
                    for (int x = low.X; x <= high.X; x++)
                    {
                        MarkBrick(x, y, z);
                    }
                }
            }
        }

        /// <summary>
        /// Marks a brick, by its place in bricks from the world's origin, where it is stored.
        /// </summary>
        private void MarkBrick(int x, int y, int z)
        {
            int bit = Wrap(x) + (Bricks * (Wrap(y) + (Bricks * Wrap(z))));
            _dirty[bit / 32] |= 1u << (bit % 32);
            _low = (Math.Min(_low.X, x), Math.Min(_low.Y, y), Math.Min(_low.Z, z));
            _high = (Math.Max(_high.X, x), Math.Max(_high.Y, y), Math.Max(_high.Z, z));
        }
    }
}
