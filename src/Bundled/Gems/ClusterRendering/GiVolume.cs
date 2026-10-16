using System.Numerics;
using System.Runtime.CompilerServices;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Where the scene's field for global illumination lies this frame and what of it is made again: nested levels around
/// the main camera, each <see cref="Resolution"/> voxels a side and twice the last one's voxel, stored wrapping around
/// so a level that moves with the camera keeps what it already holds and makes only the slabs it moved into, and around
/// what moved (<see cref="Scene.Moved"/>) the bricks it touched. Each level has a grid of <see cref="Probes"/> probes a
/// side, a share of them traced each frame: all of the finest level's every
/// <see cref="ClusterRenderingSettings.GiUpdateEvery"/> frames, and of each coarser level, whose probes light what is
/// further and larger, half as often. A probe blends what it was traced with into what it held by how long it waited,
/// so light everywhere follows a change in about the same time whatever the frame rate and however seldom a level is
/// traced (<see cref="Response"/>). Render thread.
/// </summary>
internal sealed class GiVolume : IDisposable
{
    /// <summary>
    /// Voxels a side of a level; the shaders' GiResolution.
    /// </summary>
    public const int Resolution = 64;

    /// <summary>
    /// Voxels a side of a brick, what is made again at once; a level moves a brick at a time.
    /// </summary>
    public const int Brick = 8;

    /// <summary>
    /// Probes a side of a level; the shaders' GiProbes.
    /// </summary>
    public const int Probes = 16;

    /// <summary>
    /// The most levels, which the volumes the passes make hold.
    /// </summary>
    public const int MaxLevels = 4;

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
    private uint _frame;

    public GiVolume(IGpu gpu)
    {
        Rows = new GpuArray<GpuGiLevel>(gpu, GpuBufferUsage.ComputeRead | GpuBufferUsage.GraphicsRead, 1 + MaxLevels);
        for (int level = 0; level < MaxLevels; level++)
        {
            _levels[level] = new Level();
        }
    }

    public void Dispose()
    {
        Rows.Dispose();
    }

    /// <summary>
    /// <c>GiLevels</c>: a <see cref="GpuGi"/>, then a <see cref="GpuGiLevel"/> for each level.
    /// </summary>
    public GpuArray<GpuGiLevel> Rows { get; }

    /// <summary>
    /// Rays traced this frame, every probe due's.
    /// </summary>
    public uint Rays { get; private set; }

    /// <summary>
    /// Threads the blend of this frame's probes runs, 256 a probe.
    /// </summary>
    public uint ProbeThreads { get; private set; }

    /// <summary>
    /// Nothing is held: the next frame makes every level whole.
    /// </summary>
    public void Clear()
    {
        foreach (Level level in _levels)
        {
            level.IsHeld = false;
        }

        Rays = 0;
        ProbeThreads = 0;
    }

    /// <summary>
    /// Places the levels around <paramref name="camera"/>, marks what is made again, from where they moved and from
    /// <paramref name="moved"/>, and uploads them with which probes are traced this frame and how much of what they held
    /// they keep, from the frame's <paramref name="seconds"/>.
    /// </summary>
    public void Plan(Vector3D camera, IReadOnlyList<MovedSphere> moved, float seconds, ClusterRenderingSettings settings)
    {
        int levels = Math.Clamp(settings.GiLevels, 1, MaxLevels);
        float finest = Math.Clamp(settings.GiVoxel, 0.05f, 4);
        uint every = BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(settings.GiUpdateEvery, 1, 64));
        uint rays = (uint)Math.Clamp(settings.GiRays, 8, 256);
        for (int index = 0; index < levels; index++)
        {
            Level level = _levels[index];
            float voxel = finest * (1 << index);
            level.Clear();
            (int X, int Y, int Z) origin = (Snap(camera.X, voxel), Snap(camera.Y, voxel), Snap(camera.Z, voxel));
            if (!level.IsHeld || level.Voxel != voxel)
            {
                level.MarkAll(origin);
            }
            else
            {
                level.MarkMoved(origin);
            }

            level.Voxel = voxel;
            level.Period = every << index;
            level.Hysteresis = Math.Clamp(MathF.Exp(-level.Period * seconds / (Response * MathF.Pow(MathF.Sqrt(2), index))), LeastKept, MostKept);
            level.IsHeld = true;
            foreach (MovedSphere sphere in moved)
            {
                // A voxel is metres wide, so floats hold where it is far further out than a world goes.
                Vector3 centre = sphere.Centre.ToVector3();
                float reach = sphere.Radius + (Reach * voxel);
                level.Mark(centre - new Vector3(reach), centre + new Vector3(reach));
            }
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
            Camera = camera.ToVector3(),
        };
        Rows.Set(0, Unsafe.As<GpuGi, GpuGiLevel>(ref header));
        for (int index = 0; index < MaxLevels; index++)
        {
            Rows.Set(1 + index, _levels[index].Row());
        }

        Rows.Flush();
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
    /// The first voxel of a level around <paramref name="metres"/> along one axis, a whole brick, with the camera in its
    /// middle brick.
    /// </summary>
    private static int Snap(double metres, float voxel) => ((int)Math.Floor(metres / voxel / Brick) * Brick) - (Resolution / 2);

    /// <summary>
    /// One level: where it is, whether what it holds is whole, and the bricks made again this frame.
    /// </summary>
    private sealed class Level
    {
        private (int X, int Y, int Z) _origin;
        private (int X, int Y, int Z) _low;
        private (int X, int Y, int Z) _high;
        private BrickMask _dirty;

        public bool IsHeld { get; set; }

        public float Voxel { get; set; } = 1;

        /// <summary>
        /// Frames a probe of it waits to be traced again.
        /// </summary>
        public uint Period { get; set; } = 1;

        /// <summary>
        /// How much of what a probe of it held it keeps as it takes in what it was traced with.
        /// </summary>
        public float Hysteresis { get; set; }

        public void Clear()
        {
            _dirty = default;
            _low = (int.MaxValue, int.MaxValue, int.MaxValue);
            _high = (int.MinValue, int.MinValue, int.MinValue);
        }

        /// <summary>
        /// Moves to <paramref name="origin"/> and makes every brick again.
        /// </summary>
        public void MarkAll((int X, int Y, int Z) origin)
        {
            _origin = origin;
            MarkBricks((_origin.X / Brick, _origin.Y / Brick, _origin.Z / Brick), ((_origin.X / Brick) + Bricks - 1, (_origin.Y / Brick) + Bricks - 1, (_origin.Z / Brick) + Bricks - 1));
        }

        /// <summary>
        /// Moves to <paramref name="origin"/>, making again the bricks it moved into.
        /// </summary>
        public void MarkMoved((int X, int Y, int Z) origin)
        {
            (int X, int Y, int Z) was = _origin;
            _origin = origin;
            for (int z = 0; z < Bricks; z++)
            {
                for (int y = 0; y < Bricks; y++)
                {
                    for (int x = 0; x < Bricks; x++)
                    {
                        int bx = (_origin.X / Brick) + x;
                        int by = (_origin.Y / Brick) + y;
                        int bz = (_origin.Z / Brick) + z;
                        if (!IsIn(bx * Brick, was.X) || !IsIn(by * Brick, was.Y) || !IsIn(bz * Brick, was.Z))
                        {
                            MarkBrick(bx, by, bz);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Makes again the bricks of this level that a box, in metres, touches.
        /// </summary>
        public void Mark(Vector3 min, Vector3 max)
        {
            (int X, int Y, int Z) low = (Math.Max(Floor(min.X), _origin.X / Brick), Math.Max(Floor(min.Y), _origin.Y / Brick), Math.Max(Floor(min.Z), _origin.Z / Brick));
            (int X, int Y, int Z) high = (Math.Min(Floor(max.X), ((_origin.X + Resolution) / Brick) - 1), Math.Min(Floor(max.Y), ((_origin.Y + Resolution) / Brick) - 1), Math.Min(Floor(max.Z), ((_origin.Z + Resolution) / Brick) - 1));
            MarkBricks(low, high);
        }

        public GpuGiLevel Row()
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
        /// The brick a place in metres is in, along one axis.
        /// </summary>
        private int Floor(float metres) => (int)MathF.Floor(metres / Voxel / Brick);

        private static bool IsIn(int voxel, int origin) => voxel >= origin && voxel < origin + Resolution;

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

        private static int Wrap(int brick) => ((brick % Bricks) + Bricks) % Bricks;
    }
}
