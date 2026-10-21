using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;

namespace Magic.ClusterRendering;

/// <summary>
/// Virtual shadow maps: every shadow is drawn into pages of one pool (<see cref="GpuContract.ShadowPool"/>), only the
/// pages the views' pixels read, kept from frame to frame and drawn again only where what casts into them moved, so a
/// shadow costs what is on screen, however far it reaches and however many lights cast it. The GPU marks, allocates and
/// draws the pages (Shaders/VirtualShadows/); this keeps what only the CPU can: where the key light's clipmap stands.
/// <para>
/// The shadow of the key light, whichever directional light casts the shadows (<see cref="KeyLightCopy"/>), is a
/// clipmap: <see cref="ClusterRenderingSettings.ShadowLevels"/> levels about the camera, each twice as wide as the
/// last, the finest <see cref="ClusterRenderingSettings.ShadowFinest"/> metres, each 64 pages a side, a level's window
/// moving a whole page at a time and its entries addressed round, so a page that stays in the window keeps what was
/// drawn in it. Its depth is measured along the light from a plane moved in steps of a quarter of
/// <see cref="ClusterRenderingSettings.ShadowDepth"/>; that plane moving starts every page again, and so does the light
/// turning by <see cref="ClusterRenderingSettings.ShadowTurn"/> from the way the clipmap was drawn for, which it keeps
/// till then, so a light that turns a little each frame, a sun crossing the sky, is drawn again in steps rather than
/// every frame or never.
/// Point and spot lights' pages are found by a hash of the light, the face, its mip and the page, in a table made each
/// frame from the pages the views want, which take their pool pages over from last frame's, so the table holds only
/// what is wanted and never fills with what was; a light whose shadow is small on screen takes a small page, a
/// sixteenth of a page.
/// </para>
/// <para>
/// The pool's pages' records, the clipmap's entries and the two lights' tables are one buffer (<see cref="_tables"/>),
/// so a pass that reads them binds one; <see cref="_clipmap"/> says where each starts, and which lights' table is this
/// frame's, the two swapping each frame.
/// </para>
/// Render thread.
/// </summary>
internal sealed class Shadows(GpuResources resources)
{
    /// <summary>
    /// Entries of the table point and spot lights' pages are found in: at least twice the pages the views read of them
    /// a frame, which the largest pool there is leaves room for.
    /// </summary>
    internal const int LightTableEntries = 65536;

    /// <summary>
    /// Texels a side of a page of the pool, its border of <see cref="Border"/> each side included, which a filter reads
    /// past the page's edge so it never reads its neighbour.
    /// </summary>
    private const int Page = 128;

    private const int Border = 4;

    /// <summary>
    /// Pages a side of a level of the clipmap.
    /// </summary>
    private const int Window = 64;

    /// <summary>
    /// Levels the clipmap has at most, which <see cref="_clipmap"/> has room for after its own row.
    /// </summary>
    private const int MaxLevels = 24;

    /// <summary>
    /// Bytes of a table's entry, and of a page's record.
    /// </summary>
    private const int Row = 16;

    /// <summary>
    /// Bytes of the clipmap's own row, which its levels follow, and of a level.
    /// </summary>
    private const int ClipmapBytes = 128;

    private const int LevelBytes = 32;

    /// <summary>
    /// Bytes of <see cref="_clipmap"/>: its own row, then room for the most levels.
    /// </summary>
    private const int ClipmapBufferBytes = ClipmapBytes + (MaxLevels * LevelBytes);

    private readonly GpuClipmapLevel[] _levels = new GpuClipmapLevel[MaxLevels];

    /// <summary>
    /// A record for each page of the pool, what owns it and the frame it was last read in, then the clipmap's entries,
    /// then the two lights' tables, an entry a page each.
    /// </summary>
    private GpuBuffer _tables;

    /// <summary>
    /// The clipmap this frame (<see cref="GpuClipmap"/>), then its levels (<see cref="GpuClipmapLevel"/>).
    /// </summary>
    private GpuBuffer _clipmap;

    /// <summary>
    /// The way the light travels that the clipmap's pages were drawn for, kept until it turns by the settings' step.
    /// </summary>
    private Vector3 _heldDirection;
    private float _depthOrigin = float.NaN;
    private int _levelCount;
    private bool _isSwapped;

    /// <summary>
    /// Texels a side of the pool, as last made.
    /// </summary>
    private int _poolSize;

    /// <summary>
    /// Entries of the clipmap's table and of a lights' table, which the passes that look through them run a thread for
    /// each of.
    /// </summary>
    internal int ClipmapEntries => _levelCount * Window * Window;

    /// <summary>
    /// The pool's pages kept whole, and the small pages its last quarter is split into: the pages there are to take.
    /// </summary>
    internal int WholePages => PoolPages * 3 / 4;

    internal int SmallPages => PoolPages / 4 * 16;

    /// <summary>
    /// Pages of the pool, the last quarter of which are split in sixteen small pages each.
    /// </summary>
    private int PoolPages => _poolSize / Page * (_poolSize / Page);

    /// <summary>
    /// Rows of <see cref="_tables"/> before the clipmap's entries: a record for each page, whole or small, past the
    /// whole pages' as many as the pool has pages.
    /// </summary>
    private int Records => PoolPages * 5;

    private int TableBytes => (Records + ClipmapEntries + (2 * LightTableEntries)) * Row;

    /// <summary>
    /// The levels the clipmap has as the settings ask for them, 1 to the most it has room for: what the passes' buffers
    /// are sized by too.
    /// </summary>
    internal static int GetLevelCount(ClusterRenderingSettings settings)
        => Math.Clamp(settings.ShadowLevels, 1, MaxLevels);

    /// <summary>
    /// Texels a side of the largest pool the settings' budget holds, a power of two from 1024 to 16384: a texel of
    /// depth is four bytes.
    /// </summary>
    internal static int GetPoolSize(ClusterRenderingSettings settings)
    {
        double texels = Math.Max(settings.ShadowBudget, 0) * 1024 * 1024 / sizeof(float);
        int side = 1 << (int)Math.Floor(Math.Log2(Math.Sqrt(Math.Max(texels, 1))));
        return Math.Clamp(side, 1024, 16384);
    }

    /// <summary>
    /// Places the clipmap about <paramref name="camera"/> for <paramref name="light"/>, the way the key light's light
    /// travels, swaps the lights' tables, and makes the pool and the tables again when the settings size them
    /// otherwise; <paramref name="changedLightWords"/> is how many words the bitmask of the world's lights that changed
    /// has.
    /// </summary>
    internal void Plan(
        Vector3 camera,
        Vector3 light,
        int changedLightWords,
        int moved,
        ulong frame,
        ClusterRenderingSettings settings)
    {
        int poolSize = GetPoolSize(settings);
        int levels = GetLevelCount(settings);
        bool isMade = poolSize != _poolSize || levels != _levelCount;
        if (isMade) Make(poolSize, levels);

        _isSwapped = !_isSwapped;

        Vector3 wanted = Vector3.Normalize(light);
        float turnStep = Math.Clamp(settings.ShadowTurn, 0.01f, 10) * (MathF.PI / 180);
        bool isTurned = Vector3.Dot(wanted, _heldDirection) < MathF.Cos(turnStep);
        if (isTurned) _heldDirection = wanted;

        (Vector3 direction, Vector3 right, Vector3 up) = GetClipmapAxes(_heldDirection);
        float depthRange = Math.Max(settings.ShadowDepth, 10);
        float depth = Vector3.Dot(camera, direction);
        float depthStep = depthRange / 4;
        bool isMoved = float.IsNaN(_depthOrigin) || MathF.Abs(depth - _depthOrigin) > depthStep;
        if (isMoved || isTurned) _depthOrigin = MathF.Round(depth / depthStep) * depthStep;

        float finest = Math.Max(settings.ShadowFinest, 0.5f);
        int lightTable = Records + ClipmapEntries;
        GpuClipmap clipmap = new()
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
            PoolAcross = (uint)(_poolSize / Page),
            WholePages = (uint)WholePages,
            SmallPages = (uint)SmallPages,
            Moved = (uint)moved,
            Camera = camera,
            LevelTable = (uint)Records,
            LightTable = (uint)(_isSwapped ? lightTable + LightTableEntries : lightTable),
            LightTableLast = (uint)(_isSwapped ? lightTable : lightTable + LightTableEntries),
            LightEntries = LightTableEntries,
        };

        PlaceLevels(new Vector2(Vector3.Dot(camera, right), Vector3.Dot(camera, up)), finest, levels);
        resources.Upload(_clipmap, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<GpuClipmap>(in clipmap)));
        resources.Upload(_clipmap, ClipmapBytes, MemoryMarshal.AsBytes(_levels.AsSpan(0, levels)));
    }

    /// <summary>
    /// The clipmap's axes: the way the light travels and two across it, chosen so they turn smoothly with it.
    /// </summary>
    private static (Vector3 Direction, Vector3 Right, Vector3 Up) GetClipmapAxes(Vector3 light)
    {
        Vector3 direction = Vector3.Normalize(light);
        Vector3 reference = MathF.Abs(direction.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(reference, direction));
        return (direction, right, Vector3.Cross(direction, right));
    }

    /// <summary>
    /// Each level's window about the camera, <paramref name="across"/> the light from the world's origin: its first
    /// page, a whole page from where it was, and where the camera is within it.
    /// </summary>
    private void PlaceLevels(Vector2 across, float finest, int levels)
    {
        for (int level = 0; level < levels; level++)
        {
            float pageSize = finest * (1 << level) / Window;
            int originX = (int)MathF.Floor(across.X / pageSize) - (Window / 2);
            int originY = (int)MathF.Floor(across.Y / pageSize) - (Window / 2);
            _levels[level] = new GpuClipmapLevel
            {
                OriginX = originX,
                OriginY = originY,
                PageSize = pageSize,
                Texel = pageSize / (Page - (2 * Border)),
                CameraX = across.X - (originX * pageSize),
                CameraY = across.Y - (originY * pageSize),
            };
        }
    }

    /// <summary>
    /// The pool and the tables made again in place of those they had, every page empty and every entry none: the pool
    /// <see cref="Page"/> texels a page, of depth, reverse-Z.
    /// </summary>
    private void Make(int poolSize, int levels)
    {
        _poolSize = poolSize;
        _levelCount = levels;
        resources.MakeTexture(GpuContract.ShadowPool, (uint)poolSize, (uint)poolSize);
        _tables = resources.MakeBuffer(GpuContract.ShadowTables, (uint)TableBytes);
        resources.Upload(_tables, 0, new byte[TableBytes]);
        _clipmap = resources.MakeBuffer(GpuContract.ShadowClipmap, ClipmapBufferBytes);
    }
}
