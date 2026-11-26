using System.Drawing;
using System.Numerics;
using Magic.DeferredRendering;
using Magic.DeferredRendering.World;
using Magic.Ecs;

namespace Magic.ShadowMaps;

/// <summary>
/// Fits a tile of the local atlas to each face of each local light that casts a shadow, and keeps a cache of the still
/// depth of each tile.
/// </summary>
/// <remarks>
/// <para>
/// A spot light has one perspective tile as wide as its cone, a point light the six faces of a cube, and a rect light
/// five, with none behind its face. A tile's size is the power of two that holds twice the light's range as the main
/// camera sees it, from the least to the most of the settings; while the tiles do not fit, every size steps down one
/// power of two (Unity, HDRP shadow atlas). A face whose view meets nothing that the main camera sees is not drawn.
/// </para>
/// <para>
/// A tile keeps its rectangle while its size holds, in the local atlas and in the cache alike. The cache draws a tile's
/// still casters again when the tile is new or moved, its light changed, or a still instance in its light's range
/// changed; at most a number a frame, the oldest first. Until then the atlas draws all of its casters each frame, so
/// the shadow is right at once and the cache saves time only. A tile whose cache holds its depth is copied from the
/// cache, and only its dynamic bodies are drawn over the copy.
/// </para>
/// </remarks>
internal sealed class LocalTiles
{
    /// <summary>
    /// The metres from a light to its tiles' near plane.
    /// </summary>
    internal const float NearPlane = 0.05f;

    /// <summary>
    /// The most tiles of the frame: six faces of each light of the light table.
    /// </summary>
    internal const int MaxTiles = 6 * 1024;

    /// <summary>
    /// The widest cone of a spot light's tile, in radians.
    /// </summary>
    private const float WidestCone = 170 * MathF.PI / 180;

    /// <summary>
    /// The faces of a point light's cube, as their forward axes in the light's own axes, in the order that
    /// <c>Light/LocalShadows.slang</c> picks them: the way it shines, then +X, -X, +Y, -Y and the way behind it. A rect
    /// light has the first five.
    /// </summary>
    private static readonly Vector3[] _faceAxes =
    [
        Vector3.UnitZ,
        Vector3.UnitX,
        -Vector3.UnitX,
        Vector3.UnitY,
        -Vector3.UnitY,
        -Vector3.UnitZ,
    ];

    private readonly Dictionary<(Entity Light, int Face), TileState> _states = [];

    private readonly List<(Entity Light, int Face)> _gone = [];

    private readonly List<TileState> _toPlace = [];

    private readonly List<TileState> _toCache = [];

    private readonly TileAtlas _atlas = new();

    private readonly bool[] _isTouched = new bool[MaxTiles];

    /// <summary>
    /// How many powers of two every tile's size stepped down, so that all fit.
    /// </summary>
    private int _shift;

    private long _frame;

    /// <summary>
    /// The texels along each side of the atlas and of the cache.
    /// </summary>
    internal int AtlasSize => _atlas.Size;

    internal GpuShadowView[] CacheViews { get; } = new GpuShadowView[MaxTiles];

    internal int CacheCount { get; private set; }

    internal GpuShadowView[] AtlasViews { get; } = new GpuShadowView[MaxTiles];

    internal int AtlasCount { get; private set; }

    internal GpuLightShadow[] Lights { get; } = new GpuLightShadow[MaxTiles / 6];

    internal int LightCount { get; private set; }

    internal GpuShadowTile[] Tiles { get; } = new GpuShadowTile[MaxTiles];

    internal int TileCount { get; private set; }

    /// <summary>
    /// The rectangles copied from the cache into the atlas this frame.
    /// </summary>
    internal Rectangle[] Copies { get; } = new Rectangle[MaxTiles];

    internal int CopyCount { get; private set; }

    /// <summary>
    /// The rectangles of the cache cleared this frame, before the cache draws them again.
    /// </summary>
    internal Rectangle[] Clears { get; } = new Rectangle[MaxTiles];

    internal int ClearCount { get; private set; }

    /// <summary>
    /// Fits the tiles of the world's lights for this frame, and makes the lists of views and rectangles.
    /// </summary>
    internal void Fit(in GpuWorld world, ReadOnlySpan<ChangedInstance> changed, ShadowMapsSettings settings)
    {
        _frame++;
        int atlasSize = Math.Max(
            (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(settings.AtlasResolution, 64)),
            64
        );
        int minSize = Math.Clamp(
            (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(settings.MinTileSize, 1)),
            1,
            atlasSize
        );
        int maxSize = Math.Clamp(
            (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(settings.MaxTileSize, 1)),
            minSize,
            atlasSize
        );
        if (_atlas.Size != atlasSize)
            Reset(atlasSize);

        // A light past the table's room casts no shadow.
        ReadOnlySpan<GpuLight> lights = world.LocalLights.Rows.Span;
        lights = lights[..Math.Min(lights.Length, Lights.Length)];
        ReadOnlySpan<LocalLightInfo> infos = world.LocalLights.Info.Span[..lights.Length];
        WorldEye eye = world.Eye;
        Span<Vector4> eyePlanes = stackalloc Vector4[5];
        GetPlanes(eye.ViewProjection, eyePlanes);

        // A still instance that changed in a light's range draws the light's cache again.
        Array.Clear(_isTouched);
        foreach (ChangedInstance instance in changed)
        {
            if (instance.Dynamic != GpuInstanceTraits.None)
                continue;

            for (int i = 0; i < lights.Length; i++)
            {
                float reach = GetReach(lights[i]);
                _isTouched[i] |=
                    IsTouching(lights[i].Position, reach, instance.OldCenter, instance.OldRadius)
                    || IsTouching(lights[i].Position, reach, instance.NewCenter, instance.NewRadius);
            }
        }

        // The sizes step back up once the tiles would fill no more than half of the atlas at the size above.
        if (_shift > 0 && GetArea(lights, infos, eye, minSize, maxSize, _shift - 1) * 2 <= (long)atlasSize * atlasSize)
        {
            _shift--;
            Reset(atlasSize);
        }

        while (!Place(lights, infos, eye, eyePlanes, minSize, maxSize) && _shift < 8)
        {
            _shift++;
            Reset(atlasSize);
        }

        // A tile no light has now gives its rectangle back.
        foreach (((Entity Light, int Face) key, TileState state) in _states)
        {
            if (state.SeenFrame != _frame)
                _gone.Add(key);
        }

        foreach ((Entity Light, int Face) key in _gone)
        {
            TileState state = _states[key];
            if (state.Size > 0)
                _atlas.Free(state.X, state.Y, state.Size);

            _states.Remove(key);
        }

        _gone.Clear();
        WriteRows(lights, infos, eye.Position, atlasSize, Math.Max(settings.CacheTilesPerFrame, 0));
    }

    /// <summary>
    /// Whether a sphere touches another.
    /// </summary>
    private static bool IsTouching(Vector3 center, float radius, Vector3 other, float otherRadius) =>
        otherRadius > 0 && Vector3.DistanceSquared(center, other) <= (radius + otherRadius) * (radius + otherRadius);

    /// <summary>
    /// The sphere around everything a light reaches: its range, and a rect light's half diagonal past it.
    /// </summary>
    private static float GetReach(in GpuLight light) =>
        light.Range
        + (
            light.Type == LightType.Rect
                ? 0.5f * MathF.Sqrt((light.Width * light.Width) + (light.Height * light.Height))
                : 0
        );

    private static int GetFaceCount(in GpuLight light) =>
        light.Type switch
        {
            LightType.Point => 6,
            LightType.Spot => 1,
            LightType.Rect => 5,
            _ => 0,
        };

    /// <summary>
    /// The width of a light's source, which sets how soft its shadows are: a point or spot light's diameter, or the
    /// diameter of the disc of a rect light's area.
    /// </summary>
    private static float GetLightWidth(in GpuLight light) =>
        light.Type == LightType.Rect ? 2 * MathF.Sqrt(light.Width * light.Height / MathF.PI) : 2 * light.Radius;

    /// <summary>
    /// The light's own axes: across, up, and the way it shines, as <c>Light/LocalLights.slang</c>'s
    /// <c>GetLightBasis</c> makes them.
    /// </summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Forward) GetBasis(in GpuLight light)
    {
        Vector3 cross = Vector3.Cross(Vector3.UnitY, light.Direction);
        Vector3 right = cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : Vector3.UnitX;

        return (right, Vector3.Cross(light.Direction, right), light.Direction);
    }

    /// <summary>
    /// The axes of face <paramref name="face"/> of a light, with the frame's sides as a camera's are, so a triangle keeps
    /// its winding, and the half angle of its view.
    /// </summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Forward, float HalfAngle) GetFace(in GpuLight light, int face)
    {
        if (light.Type == LightType.Spot)
        {
            float cone = Math.Min(2 * MathF.Acos(Math.Clamp(light.CosOuter, -1, 1)), WidestCone);
            (Vector3 right, Vector3 up) = GetSides(light.Direction, Vector3.UnitY);

            return (right, up, light.Direction, cone / 2);
        }

        (Vector3 basisRight, Vector3 basisUp, Vector3 basisForward) = GetBasis(light);
        Vector3 axis = _faceAxes[face];
        Vector3 forward = (basisRight * axis.X) + (basisUp * axis.Y) + (basisForward * axis.Z);
        (Vector3 faceRight, Vector3 faceUp) = GetSides(forward, MathF.Abs(axis.Y) > 0.5f ? basisForward : basisUp);

        return (faceRight, faceUp, forward, MathF.PI / 4);
    }

    /// <summary>
    /// The across and up axes of a view along <paramref name="forward"/>, up as near <paramref name="upHint"/> as it can
    /// be.
    /// </summary>
    private static (Vector3 Right, Vector3 Up) GetSides(Vector3 forward, Vector3 upHint)
    {
        Vector3 hint = MathF.Abs(Vector3.Dot(forward, upHint)) < 0.999f ? upHint : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(hint, forward));

        return (right, Vector3.Cross(forward, right));
    }

    /// <summary>
    /// The sides and near plane of a view-projection for row vectors, each a unit normal that points in and an offset.
    /// </summary>
    private static void GetPlanes(in Matrix4x4 viewProjection, Span<Vector4> planes)
    {
        Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
        planes[0] = ToPlane(c3 + c0);
        planes[1] = ToPlane(c3 - c0);
        planes[2] = ToPlane(c3 + c1);
        planes[3] = ToPlane(c3 - c1);
        planes[4] = ToPlane(c3 - c2);
    }

    private static Vector4 ToPlane(Vector4 plane)
    {
        float length = new Vector3(plane.X, plane.Y, plane.Z).Length();

        return length > 0 ? plane / length : plane;
    }

    /// <summary>
    /// Whether the view of a face, from its apex out to the light's reach, meets the main camera's view.
    /// </summary>
    private static bool IsSeen(
        Vector3 apex,
        float reach,
        (Vector3 Right, Vector3 Up, Vector3 Forward, float HalfAngle) face,
        ReadOnlySpan<Vector4> eyePlanes
    )
    {
        float side = reach * MathF.Tan(face.HalfAngle);
        Vector3 far = apex + (face.Forward * reach);
        Span<Vector3> corners =
        [
            apex,
            far + (face.Right * side) + (face.Up * side),
            far + (face.Right * side) - (face.Up * side),
            far - (face.Right * side) + (face.Up * side),
            far - (face.Right * side) - (face.Up * side),
        ];
        foreach (Vector4 plane in eyePlanes)
        {
            bool isOutside = true;
            foreach (Vector3 corner in corners)
            {
                if ((plane.X * corner.X) + (plane.Y * corner.Y) + (plane.Z * corner.Z) + plane.W >= 0)
                {
                    isOutside = false;
                    break;
                }
            }

            if (isOutside)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The size of a light's tiles: the power of two that holds twice its range as the main camera sees it, stepped
    /// down <paramref name="shift"/> times, from <paramref name="minSize"/> to <paramref name="maxSize"/>.
    /// </summary>
    private static int GetTileSize(in GpuLight light, in WorldEye eye, int minSize, int maxSize, int shift)
    {
        float reach = GetReach(light);
        float distance = Vector3.Distance(light.Position, eye.Position);
        float pixels = distance <= reach ? maxSize : 2 * reach / distance * eye.PixelsPerRadian;
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp((int)MathF.Ceiling(pixels), 1, maxSize));

        return Math.Max(Math.Clamp(size, minSize, maxSize) >> shift, minSize);
    }

    /// <summary>
    /// The texels that every light's tiles take at <paramref name="shift"/>.
    /// </summary>
    private static long GetArea(
        ReadOnlySpan<GpuLight> lights,
        ReadOnlySpan<LocalLightInfo> infos,
        in WorldEye eye,
        int minSize,
        int maxSize,
        int shift
    )
    {
        long area = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            if (infos[i].IsShadowless)
                continue;

            long size = GetTileSize(lights[i], eye, minSize, maxSize, shift);
            area += size * size * GetFaceCount(lights[i]);
        }

        return area;
    }

    /// <summary>
    /// The view of face <paramref name="face"/> of a light, relative to <paramref name="anchor"/>: a perspective with
    /// reversed depth and no far plane, whose depth is the near plane over the view depth, and planes that reach as
    /// far as the light does.
    /// </summary>
    private static GpuShadowView CreateView(in GpuLight light, int face, Vector3 anchor, TileState state, int atlasSize)
    {
        (Vector3 right, Vector3 up, Vector3 forward, float halfAngle) = GetFace(light, face);
        float focal = 1 / MathF.Tan(halfAngle);
        Vector3 apex = light.Position - anchor;
        Matrix4x4 viewProjection = new(
            right.X * focal,
            up.X * focal,
            0,
            forward.X,
            right.Y * focal,
            up.Y * focal,
            0,
            forward.Y,
            right.Z * focal,
            up.Z * focal,
            0,
            forward.Z,
            -Vector3.Dot(apex, right) * focal,
            -Vector3.Dot(apex, up) * focal,
            NearPlane,
            -Vector3.Dot(apex, forward)
        );
        Span<Vector4> planes = stackalloc Vector4[5];
        GetPlanes(viewProjection, planes);
        float reach = GetReach(light);
        int size = state.Size;

        return new GpuShadowView
        {
            ViewProjection = viewProjection,
            Plane0 = planes[0],
            Plane1 = planes[1],
            Plane2 = planes[2],
            Plane3 = planes[3],
            Plane4 = planes[4],
            Plane5 = new Vector4(-forward, Vector3.Dot(apex, forward) + reach),
            RectX = (uint)state.X,
            RectY = (uint)state.Y,
            RectWidth = (uint)size,
            RectHeight = (uint)size,
            AtlasTransform = new Vector4(
                (float)size / atlasSize,
                (float)size / atlasSize,
                (((2f * state.X) + size) / atlasSize) - 1,
                1 - (((2f * state.Y) + size) / atlasSize)
            ),
            Towards = -forward,
            TexelSize = 2 / (focal * size),
            IsOrthographic = 0,
            DepthRange = NearPlane,
            Casters = ShadowCasters.All,
        };
    }

    /// <summary>
    /// Forgets every rectangle, so every tile is placed and cached anew.
    /// </summary>
    private void Reset(int atlasSize)
    {
        _atlas.Reset(atlasSize);
        foreach (TileState state in _states.Values)
        {
            state.Size = 0;
            state.IsCached = false;
            state.DirtySince = Math.Min(state.DirtySince, _frame);
        }
    }

    /// <summary>
    /// Gives each seen face of each light a rectangle, keeping the one it had while its size holds; false where they do
    /// not all fit.
    /// </summary>
    private bool Place(
        ReadOnlySpan<GpuLight> lights,
        ReadOnlySpan<LocalLightInfo> infos,
        in WorldEye eye,
        ReadOnlySpan<Vector4> eyePlanes,
        int minSize,
        int maxSize
    )
    {
        _toPlace.Clear();
        for (int i = 0; i < lights.Length; i++)
        {
            if (infos[i].IsShadowless)
                continue;

            GpuLight light = lights[i];
            float reach = GetReach(light);
            int size = GetTileSize(light, eye, minSize, maxSize, _shift);
            Vector3 apex = light.Position - eye.Position;
            for (int face = 0; face < GetFaceCount(light); face++)
            {
                if (!IsSeen(apex, reach, GetFace(light, face), eyePlanes))
                    continue;

                (Entity, int) key = (infos[i].Entity, face);
                if (!_states.TryGetValue(key, out TileState? state))
                {
                    state = new TileState { DirtySince = _frame };
                    _states[key] = state;
                }

                state.SeenFrame = _frame;
                state.LightIndex = i;
                if (state.Light != light || _isTouched[i])
                {
                    state.Light = light;
                    MarkDirty(state);
                }

                if (state.Size != size)
                {
                    if (state.Size > 0)
                        _atlas.Free(state.X, state.Y, state.Size);

                    state.Size = 0;
                    state.WantedSize = size;
                    MarkDirty(state);
                    _toPlace.Add(state);
                }
            }
        }

        // The largest first, so the small ones fill what is left between them.
        _toPlace.Sort(static (a, b) => b.WantedSize.CompareTo(a.WantedSize));
        foreach (TileState state in _toPlace)
        {
            if (!_atlas.TryAllocate(state.WantedSize, out int x, out int y))
                return false;

            (state.X, state.Y, state.Size) = (x, y, state.WantedSize);
        }

        return true;
    }

    private void MarkDirty(TileState state)
    {
        if (state.IsCached || state.DirtySince < 0)
            state.DirtySince = _frame;

        state.IsCached = false;
    }

    /// <summary>
    /// Writes each light's row and each tile's, and the lists of views to draw and rectangles to copy and clear.
    /// </summary>
    private void WriteRows(
        ReadOnlySpan<GpuLight> lights,
        ReadOnlySpan<LocalLightInfo> infos,
        Vector3 anchor,
        int atlasSize,
        int cacheTilesPerFrame
    )
    {
        (LightCount, TileCount, AtlasCount, CacheCount, CopyCount, ClearCount) = (lights.Length, 0, 0, 0, 0, 0);
        _toCache.Clear();
        for (int i = 0; i < lights.Length; i++)
        {
            GpuLight light = lights[i];
            if (infos[i].IsShadowless)
            {
                Lights[i] = new GpuLightShadow { FirstTile = GpuLightShadow.NoTile };
                continue;
            }

            int faces = GetFaceCount(light);
            Lights[i] = new GpuLightShadow
            {
                FirstTile = (uint)TileCount,
                FaceCount = (uint)faces,
                LightWidth = GetLightWidth(light),
            };
            for (int face = 0; face < faces; face++)
            {
                int tile = TileCount++;
                if (
                    !_states.TryGetValue((infos[i].Entity, face), out TileState? state)
                    || state.SeenFrame != _frame
                    || state.Size == 0
                )
                {
                    Tiles[tile] = default;
                    continue;
                }

                GpuShadowView view = CreateView(light, face, anchor, state, atlasSize);
                Tiles[tile] = new GpuShadowTile
                {
                    ViewProjection = view.ViewProjection,
                    RectX = view.RectX,
                    RectY = view.RectY,
                    RectWidth = view.RectWidth,
                    RectHeight = view.RectHeight,
                };
                state.View = view;
                Rectangle rect = new(state.X, state.Y, state.Size, state.Size);
                if (state.IsCached)
                {
                    Copies[CopyCount++] = rect;
                    AtlasViews[AtlasCount++] = view with { Casters = ShadowCasters.Moving };
                }
                else
                {
                    AtlasViews[AtlasCount++] = view;
                    _toCache.Add(state);
                }
            }
        }

        // The oldest requests of the cache first, so none waits for ever; each is cached from the next frame on.
        _toCache.Sort(static (a, b) => a.DirtySince.CompareTo(b.DirtySince));
        int cached = Math.Min(_toCache.Count, cacheTilesPerFrame);
        for (int i = 0; i < cached; i++)
        {
            TileState state = _toCache[i];
            CacheViews[CacheCount++] = state.View with { Casters = ShadowCasters.Still };
            Clears[ClearCount++] = new Rectangle(state.X, state.Y, state.Size, state.Size);
            state.IsCached = true;
            state.DirtySince = -1;
        }
    }

    /// <summary>
    /// What the fit keeps of one face of one light from frame to frame.
    /// </summary>
    private sealed class TileState
    {
        public int X;

        public int Y;

        /// <summary>
        /// The side of its rectangle, 0 while it has none.
        /// </summary>
        public int Size;

        public int WantedSize;

        public int LightIndex;

        public long SeenFrame;

        /// <summary>
        /// Whether the cache holds the tile's still depth as it is now.
        /// </summary>
        public bool IsCached;

        /// <summary>
        /// The frame since which the cache waits to draw the tile again, -1 while it does not.
        /// </summary>
        public long DirtySince;

        /// <summary>
        /// The light as the cache drew it.
        /// </summary>
        public GpuLight Light;

        public GpuShadowView View;
    }
}

/// <summary>
/// Square rectangles of a square atlas whose sides are powers of two, each split in four as smaller ones are asked
/// for, and joined again once all four are free.
/// </summary>
internal sealed class TileAtlas
{
    private readonly List<HashSet<(int X, int Y)>> _free = [];

    internal int Size { get; private set; }

    /// <summary>
    /// Frees every rectangle: the atlas is one of <paramref name="size"/>.
    /// </summary>
    internal void Reset(int size)
    {
        Size = size;
        _free.Clear();
        for (int side = size; side >= 1; side /= 2)
        {
            _free.Add([]);
        }

        _free[0].Add((0, 0));
    }

    /// <summary>
    /// Takes a free rectangle of <paramref name="side"/>; false where none is left.
    /// </summary>
    internal bool TryAllocate(int side, out int x, out int y)
    {
        int level = GetLevel(side);
        if (level < 0 || !TryTake(level, out (int X, int Y) block))
        {
            (x, y) = (0, 0);

            return false;
        }

        (x, y) = block;

        return true;
    }

    /// <summary>
    /// Gives back a rectangle that <see cref="TryAllocate"/> took.
    /// </summary>
    internal void Free(int x, int y, int side)
    {
        int level = GetLevel(side);
        if (level < 0)
            return;

        // While its three siblings are free too, the four join into their parent.
        while (level > 0)
        {
            int parent = side * 2;
            int px = x & ~(parent - 1);
            int py = y & ~(parent - 1);
            HashSet<(int X, int Y)> free = _free[level];
            (int, int) a = (px, py);
            (int, int) b = (px + side, py);
            (int, int) c = (px, py + side);
            (int, int) d = (px + side, py + side);
            (int, int) self = (x, y);
            bool isJoined =
                (self == a || free.Contains(a))
                && (self == b || free.Contains(b))
                && (self == c || free.Contains(c))
                && (self == d || free.Contains(d));
            if (!isJoined)
                break;

            free.Remove(a);
            free.Remove(b);
            free.Remove(c);
            free.Remove(d);
            (x, y, side, level) = (px, py, parent, level - 1);
        }

        _free[level].Add((x, y));
    }

    private int GetLevel(int side)
    {
        if (side <= 0 || side > Size || !BitOperations.IsPow2(side))
            return -1;

        return BitOperations.Log2((uint)Size) - BitOperations.Log2((uint)side);
    }

    private bool TryTake(int level, out (int X, int Y) block)
    {
        HashSet<(int X, int Y)> free = _free[level];
        if (free.Count > 0)
        {
            // The lowest, so the atlas fills from its top left and the same frames place tiles in the same places.
            block = (int.MaxValue, int.MaxValue);
            foreach ((int X, int Y) candidate in free)
            {
                if (candidate.Y < block.Y || (candidate.Y == block.Y && candidate.X < block.X))
                    block = candidate;
            }

            free.Remove(block);

            return true;
        }

        if (level == 0 || !TryTake(level - 1, out (int X, int Y) parent))
        {
            block = default;

            return false;
        }

        int side = Size >> level;
        free.Add((parent.X + side, parent.Y));
        free.Add((parent.X, parent.Y + side));
        free.Add((parent.X + side, parent.Y + side));
        block = parent;

        return true;
    }
}
