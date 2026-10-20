using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Virtual shadow maps: every shadow is drawn into pages of one pool (<see cref="Pool"/>), only the pages the views'
/// pixels read, kept from frame to frame and drawn again only where what casts into them moved, so a shadow costs what
/// is on screen, however far it reaches and however many lights cast it. The GPU marks, allocates and draws the pages
/// (Shaders/VirtualShadows/); this keeps what only the CPU can: where the sun's clipmap stands.
/// <para>
/// The sun's shadow is a clipmap: <see cref="ClusterRenderingSettings.ShadowLevels"/> levels about the camera, each
/// twice as wide as the last, the finest <see cref="ClusterRenderingSettings.ShadowFinest"/> metres, each 64 pages a
/// side, a level's window moving a whole page at a time and its entries addressed round, so a page that stays in the
/// window keeps what was drawn in it. Its depth is measured along the sun from a plane moved in steps of a quarter of
/// <see cref="ClusterRenderingSettings.ShadowDepth"/>; the sun turning or that plane moving starts every page again.
/// Point and spot lights' pages are found by a hash of the light, the face, its mip and the page, in a table made each
/// frame from the pages the views want, which take their pool pages over from last frame's, so the table holds only
/// what is wanted and never fills with what was; a light whose shadow is small on screen takes a small page, a
/// sixteenth of a page.
/// </para>
/// <para>
/// The pool's pages' records, the sun's entries and the two lights' tables are one buffer (<see cref="Tables"/>), so a
/// pass that reads them binds one; <see cref="Clipmap"/> says where each starts, and which lights' table is this
/// frame's, the two swapping each frame.
/// </para>
/// Render thread.
/// </summary>
internal sealed class Shadows(IGpu gpu) : IDisposable
{
    /// <summary>
    /// Texels a side of a page of the pool, its border of <see cref="Border"/> each side included, which a filter reads
    /// past the page's edge so it never reads its neighbour.
    /// </summary>
    private const int Page = 128;

    private const int Border = 4;

    /// <summary>
    /// Pages a side of a level of the sun's clipmap.
    /// </summary>
    private const int Window = 64;

    /// <summary>
    /// Levels the clipmap has at most, which <see cref="Clipmap"/> has room for after its own row.
    /// </summary>
    private const int MaxLevels = 24;

    /// <summary>
    /// Entries of the table point and spot lights' pages are found in: at least twice the pages the views read of them a
    /// frame, which the largest pool there is leaves room for.
    /// </summary>
    internal const int LightTableEntries = 65536;

    /// <summary>
    /// Bytes of a table's entry, and of a page's record.
    /// </summary>
    private const int Row = 16;

    /// <summary>
    /// Bytes of the clipmap's own row, which its levels follow, and of a level.
    /// </summary>
    private const int ClipmapBytes = 128;

    private const int LevelBytes = 32;

    private readonly GpuSunLevel[] _levels = new GpuSunLevel[MaxLevels];
    private Vector3 _sunDirection;
    private float _depthOrigin = float.NaN;
    private int _levelCount;
    private bool _isSwapped;

    public void Dispose()
    {
        Release();
    }

    /// <summary>
    /// The pool every page is drawn into: depth, reverse-Z, <see cref="Page"/> texels a page.
    /// </summary>
    internal GpuTexture Pool { get; private set; }

    /// <summary>
    /// A record for each page of the pool, what owns it and the frame it was last read in, then the sun's entries, then
    /// the two lights' tables, an entry a page each.
    /// </summary>
    internal GpuBuffer Tables { get; private set; }

    /// <summary>
    /// The clipmap this frame (<see cref="GpuSunClipmap"/>), then its levels (<see cref="GpuSunLevel"/>).
    /// </summary>
    internal GpuBuffer Clipmap { get; private set; }

    private int PoolSize { get; set; }

    /// <summary>
    /// Entries of the sun's table and of a lights' table, which the passes that look through them run a thread for each
    /// of.
    /// </summary>
    internal int SunEntries => _levelCount * Window * Window;

    /// <summary>
    /// Pages of the pool, the last quarter of which are split in sixteen small pages each.
    /// </summary>
    private int PoolPages => PoolSize / Page * (PoolSize / Page);

    /// <summary>
    /// The pool's pages kept whole, and the small pages its last quarter is split into: the pages there are to take.
    /// </summary>
    internal int WholePages => PoolPages * 3 / 4;

    internal int SmallPages => PoolPages / 4 * 16;

    /// <summary>
    /// Rows of <see cref="Tables"/> before the sun's entries: a record for each page, whole or small, past the whole
    /// pages' as many as the pool has pages.
    /// </summary>
    private int Records => PoolPages * 5;

    private int TableRows => Records + SunEntries + (2 * LightTableEntries);

    /// <summary>
    /// Places the sun's clipmap about <paramref name="camera"/> for <paramref name="sun"/>, the way its light travels,
    /// swaps the lights' tables, and makes the pool and the tables again when the settings size them otherwise;
    /// <paramref name="changedLightWords"/> is how many words the scene's bitmask of the lights that changed has.
    /// </summary>
    internal void Plan(Vector3 camera, Vector3 sun, int changedLightWords, int moved, ulong frame, ClusterRenderingSettings settings)
    {
        int poolSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(settings.ShadowPool, 1024, 16384));
        int levels = Math.Clamp(settings.ShadowLevels, 1, MaxLevels);
        bool isMade = poolSize != PoolSize || levels != _levelCount;
        if (isMade) Make(poolSize, levels);

        _isSwapped = !_isSwapped;

        // The sun's frame: its direction and two axes across it, chosen so they turn smoothly with it.
        Vector3 direction = Vector3.Normalize(sun);
        Vector3 reference = MathF.Abs(direction.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(reference, direction));
        Vector3 up = Vector3.Cross(direction, right);
        float depthRange = Math.Max(settings.ShadowDepth, 10);
        float depth = Vector3.Dot(camera, direction);
        float depthStep = depthRange / 4;
        bool isTurned = Vector3.Dot(direction, _sunDirection) < 0.999999f;
        bool isMoved = float.IsNaN(_depthOrigin) || MathF.Abs(depth - _depthOrigin) > depthStep;
        if (isMoved || isTurned) _depthOrigin = MathF.Round(depth / depthStep) * depthStep;

        _sunDirection = direction;
        float finest = Math.Max(settings.ShadowFinest, 0.5f);
        int lightTable = Records + SunEntries;
        GpuSunClipmap clipmap = new()
        {
            Right = right,
            Levels = (uint)levels,
            Up = up,

            // Never 0, which every entry of a table just made holds, so nothing is wanted before it is marked.
            Frame = (uint)(frame % (uint.MaxValue - 1)) + 1,
            Direction = direction,
            AllDirty = isMade || isMoved || isTurned ? 1u : 0u,
            CameraDepth = depth - _depthOrigin,
            DepthRange = depthRange,
            Finest = finest,
            ChangedLightWords = (uint)changedLightWords,
            PoolAcross = (uint)(PoolSize / Page),
            WholePages = (uint)WholePages,
            SmallPages = (uint)SmallPages,
            Moved = (uint)moved,
            Camera = camera,
            SunTable = (uint)Records,
            LightTable = (uint)(_isSwapped ? lightTable + LightTableEntries : lightTable),
            LightTableLast = (uint)(_isSwapped ? lightTable : lightTable + LightTableEntries),
            LightEntries = LightTableEntries,
        };

        Vector2 across = new(Vector3.Dot(camera, right), Vector3.Dot(camera, up));
        for (int level = 0; level < levels; level++)
        {
            float pageSize = finest * (1 << level) / Window;
            int originX = (int)MathF.Floor(across.X / pageSize) - (Window / 2);
            int originY = (int)MathF.Floor(across.Y / pageSize) - (Window / 2);
            _levels[level] = new GpuSunLevel
            {
                OriginX = originX,
                OriginY = originY,
                PageSize = pageSize,
                Texel = pageSize / (Page - (2 * Border)),
                CameraX = across.X - (originX * pageSize),
                CameraY = across.Y - (originY * pageSize),
            };
        }

        gpu.Upload(Clipmap, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<GpuSunClipmap>(in clipmap)));
        gpu.Upload(Clipmap, ClipmapBytes, MemoryMarshal.AsBytes(_levels.AsSpan(0, levels)));
    }

    /// <summary>
    /// Names the pool and the tables in <paramref name="frame"/>, for the passes that draw and read the shadows; nothing
    /// before the first <see cref="Plan"/> makes them.
    /// </summary>
    internal void Bind(ResourceSet frame)
    {
        if (PoolSize == 0) return;

        frame.Set("ShadowPool", BoundResource.Of(Pool, (uint)PoolSize, (uint)PoolSize));
        frame.Set("ShadowTables", BoundResource.Of(Tables, (uint)(TableRows * Row)));
        frame.Set("SunClipmap", BoundResource.Of(Clipmap, ClipmapBytes + (MaxLevels * LevelBytes)));
    }

    /// <summary>
    /// The pool and the tables made again, every page empty and every entry none.
    /// </summary>
    private void Make(int poolSize, int levels)
    {
        Release();
        PoolSize = poolSize;
        _levelCount = levels;
        Pool = gpu.CreateTexture(new GpuTextureDescription(GpuFormat.D32Float, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampled, (uint)poolSize, (uint)poolSize));
        GpuBufferUsage written = GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite | GpuBufferUsage.GraphicsRead;
        int bytes = TableRows * Row;
        Tables = gpu.CreateBuffer(written, (uint)bytes);
        gpu.Upload(Tables, 0, new byte[bytes]);
        Clipmap = gpu.CreateBuffer(GpuBufferUsage.ComputeRead | GpuBufferUsage.GraphicsRead, ClipmapBytes + (MaxLevels * LevelBytes));
    }

    private void Release()
    {
        if (PoolSize == 0) return;

        gpu.Release(Pool);
        gpu.Release(Tables);
        gpu.Release(Clipmap);
        PoolSize = 0;
    }
}

/// <summary>
/// The sun's clipmap this frame (Shaders/Include/VirtualShadows.hlsli's GpuSunClipmap), its levels after it: its frame,
/// the axes across it and the way its light travels, how many levels it has, the frame's number (pages last read before
/// it are free to take), whether every page starts again, how far along the sun the camera is from the plane depth is
/// measured from, how deep a page reaches either side of it, the finest level's width, the words of the changed lights'
/// bits, the pool's pages a side, the pages of it whole and the small pages the rest is split into, how many spheres
/// moved, where the main camera is, which the clipmap is placed about, and the rows of the tables where the sun's
/// entries, this frame's lights' table and the last frame's start, and how many entries a lights' table has.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSunClipmap
{
    internal Vector3 Right;
    internal uint Levels;
    internal Vector3 Up;
    internal uint Frame;
    internal Vector3 Direction;
    internal uint AllDirty;
    internal float CameraDepth;
    internal float DepthRange;
    internal float Finest;
    internal uint ChangedLightWords;
    internal uint PoolAcross;
    internal uint WholePages;
    internal uint SmallPages;
    internal uint Moved;
    internal Vector3 Camera;
    internal uint SunTable;
    internal uint LightTable;
    internal uint LightTableLast;
    internal uint LightEntries;
    internal uint Padding0;
    internal uint Padding1;
    internal uint Padding2;
    internal uint Padding3;
    internal uint Padding4;
}

/// <summary>
/// One level of the sun's clipmap: its window's first page, counted from the sun's frame's origin, metres a page and a
/// texel, and where the camera is across the sun from the window's corner, in metres.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSunLevel
{
    internal int OriginX;
    internal int OriginY;
    internal float PageSize;
    internal float Texel;
    internal float CameraX;
    internal float CameraY;
    internal float Padding0;
    internal float Padding1;
}
