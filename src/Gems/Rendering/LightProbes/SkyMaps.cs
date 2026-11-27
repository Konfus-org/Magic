using System.Drawing;
using System.Numerics;
using Magic.DeferredRendering;
using Magic.DeferredRendering.World;

namespace Magic.LightProbes;

/// <summary>
/// One set of the sky's direction maps, near or far: an orthographic depth map for each of a fixed set of directions on
/// the upper hemisphere, of a box around the main camera, each in its square of one atlas.
/// </summary>
/// <remarks>
/// <para>
/// The directions lie on a Fibonacci spiral, cosine-weighted, so their mean weighted by nothing is the sky's light on a
/// level surface. Each map is the square around the box as its direction sees it, from a near plane far towards the sky,
/// so a caster above the box, such as a cliff, is still drawn, at the near plane. The box is snapped to a grid, so the
/// maps keep still as the camera moves inside a cell.
/// </para>
/// <para>
/// A map keeps the box it was drawn for. When the camera's box moves, the maps are drawn again at most a number a frame,
/// those of the oldest box first; until then each is tested by its own view. When the world changes, the rectangle of
/// each map that the old and the new bounds of a still instance cover waits to be drawn again: at most a number of maps
/// a frame, in a fixed round, so each change is whole in a bounded number of frames. A new change to a map that waits
/// joins its rectangle. A frame with budget left over also draws one map whole again, in a fixed round.
/// </para>
/// </remarks>
internal sealed class SkyMaps
{
    /// <summary>
    /// The metres towards the sky from the box's centre that a map's near plane is at.
    /// </summary>
    private const float CasterReach = 1000;

    private readonly MapState[] _maps;

    private readonly Vector3[] _directions;

    private int _round;

    /// <summary>
    /// The map that is drawn whole again next, when a frame has budget left over.
    /// </summary>
    private int _refresh;

    internal SkyMaps(int count, int columns, float region, float snap, int mapSize)
    {
        Count = count;
        Columns = columns;
        Region = region;
        Snap = snap;
        MapSize = mapSize;
        AtlasWidth = columns * mapSize;
        AtlasHeight = (count + columns - 1) / columns * mapSize;
        _maps = new MapState[count];
        _directions = new Vector3[count];
        Maps = new GpuShadowView[count];
        Draws = new GpuShadowView[count];
        Clears = new Rectangle[count];
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < count; i++)
        {
            // Cosine-weighted: the sine of the angle from the zenith is the square root of an even share.
            float share = (i + 0.5f) / count;
            float sine = MathF.Sqrt(share);
            float azimuth = i * golden;
            _directions[i] = new Vector3(sine * MathF.Cos(azimuth), MathF.Sqrt(1 - share), sine * MathF.Sin(azimuth));
            _maps[i] = new MapState();
        }
    }

    internal int Count { get; }

    internal int Columns { get; }

    internal float Region { get; }

    internal float Snap { get; }

    internal int MapSize { get; }

    internal int AtlasWidth { get; }

    internal int AtlasHeight { get; }

    /// <summary>
    /// Each whole map's view, relative to the anchor of the last fit; a rectangle of no width where its depth is not
    /// drawn.
    /// </summary>
    internal GpuShadowView[] Maps { get; }

    /// <summary>
    /// The views this frame draws: whole maps and parts of maps.
    /// </summary>
    internal GpuShadowView[] Draws { get; }

    internal int DrawCount { get; private set; }

    /// <summary>
    /// The rectangles of the atlas this frame clears before it draws them again.
    /// </summary>
    internal Rectangle[] Clears { get; }

    internal int ClearCount { get; private set; }

    /// <summary>
    /// Forgets every map, so each is drawn whole again.
    /// </summary>
    internal void Reset()
    {
        foreach (MapState map in _maps)
        {
            map.IsDrawn = false;
            map.Pending = Rectangle.Empty;
        }
    }

    /// <summary>
    /// Fits the maps around <paramref name="eye"/> for this frame: marks what <paramref name="changed"/> touched, picks
    /// the whole maps and the parts this frame draws, and writes every view relative to <paramref name="anchor"/>.
    /// </summary>
    internal void Fit(
        Vector3 eye,
        Vector3 anchor,
        ReadOnlySpan<ChangedInstance> changed,
        int wholePerFrame,
        int partsPerFrame
    )
    {
        Vector3 box = new(
            MathF.Round(eye.X / Snap) * Snap,
            MathF.Round(eye.Y / Snap) * Snap,
            MathF.Round(eye.Z / Snap) * Snap
        );

        // What a still instance changed waits in each drawn map it touches, its old and its new bounds both.
        foreach (ChangedInstance instance in changed)
        {
            if (instance.Dynamic != GpuInstanceTraits.None)
                continue;

            for (int i = 0; i < Count; i++)
            {
                MapState map = _maps[i];
                if (!map.IsDrawn)
                    continue;

                Mark(map, instance.OldCenter, instance.OldRadius);
                Mark(map, instance.NewCenter, instance.NewRadius);
            }
        }

        (DrawCount, ClearCount) = (0, 0);

        // Whole maps first: those never drawn, then those of the oldest box, a number a frame.
        int whole = 0;
        for (int pass = 0; pass < 2 && whole < wholePerFrame; pass++)
        {
            for (int i = 0; i < Count && whole < wholePerFrame; i++)
            {
                MapState map = _maps[i];
                bool isDue = pass == 0 ? !map.IsDrawn : map.IsDrawn && map.Box != box && !map.IsWholeThisFrame;
                if (!isDue)
                    continue;

                map.Box = box;
                map.IsDrawn = true;
                map.IsWholeThisFrame = true;
                map.Pending = Rectangle.Empty;
                AddDraw(CreateView(i, box, anchor), GetRect(i));
                whole++;
            }
        }

        // A frame with budget left over draws one more map whole again, in a fixed round, so what a change did not
        // mark, such as a model or its clusters that arrived after the map was drawn, is right again in a few seconds.
        if (whole < wholePerFrame)
        {
            MapState map = _maps[_refresh];
            if (map.IsDrawn && !map.IsWholeThisFrame)
            {
                map.IsWholeThisFrame = true;
                map.Pending = Rectangle.Empty;
                AddDraw(CreateView(_refresh, map.Box, anchor), GetRect(_refresh));
            }

            _refresh = (_refresh + 1) % Count;
        }

        // Then the parts that wait, a number of maps a frame, in a fixed round.
        int parts = 0;
        for (int step = 0; step < Count && parts < partsPerFrame; step++)
        {
            int i = (_round + step) % Count;
            MapState map = _maps[i];
            if (map.IsWholeThisFrame || map.Pending.IsEmpty)
                continue;

            GpuShadowView view = CreateView(i, map.Box, anchor);
            AddDraw(CreatePart(view, map.Pending), Offset(map.Pending, i));
            map.Pending = Rectangle.Empty;
            parts++;
        }

        _round = (_round + Math.Max(partsPerFrame, 1)) % Count;
        for (int i = 0; i < Count; i++)
        {
            MapState map = _maps[i];
            map.IsWholeThisFrame = false;
            Maps[i] = map.IsDrawn ? CreateView(i, map.Box, anchor) : default;
        }
    }

    private static Vector4 ToPlane(Vector4 plane)
    {
        float length = new Vector3(plane.X, plane.Y, plane.Z).Length();

        return length > 0 ? plane / length : plane;
    }

    /// <summary>
    /// The texel rectangle of map <paramref name="index"/> in the atlas.
    /// </summary>
    private Rectangle GetRect(int index) => new(index % Columns * MapSize, index / Columns * MapSize, MapSize, MapSize);

    private Rectangle Offset(Rectangle part, int index)
    {
        Rectangle rect = GetRect(index);

        return new Rectangle(rect.X + part.X, rect.Y + part.Y, part.Width, part.Height);
    }

    private void AddDraw(GpuShadowView view, Rectangle rect)
    {
        Draws[DrawCount++] = view;
        Clears[ClearCount++] = rect;
    }

    /// <summary>
    /// Joins the texels of a map that a sphere covers, rounded out to whole texels, to the rectangle that waits.
    /// </summary>
    private void Mark(MapState map, Vector3 center, float radius)
    {
        if (radius <= 0)
            return;

        // In the map's own axes, from its box's centre: across, up, and the half side of its square.
        (Vector3 right, Vector3 up, float half) = (map.View.Right, map.View.Up, map.View.Half);
        Vector3 offset = center - map.Box;
        float x = Vector3.Dot(offset, right);
        float y = Vector3.Dot(offset, up);
        float texel = 2 * half / MapSize;
        int left = (int)MathF.Floor((x - radius + half) / texel);
        int rightEdge = (int)MathF.Ceiling((x + radius + half) / texel);
        int top = (int)MathF.Floor((half - y - radius) / texel);
        int bottom = (int)MathF.Ceiling((half - y + radius) / texel);
        left = Math.Clamp(left, 0, MapSize);
        rightEdge = Math.Clamp(rightEdge, 0, MapSize);
        top = Math.Clamp(top, 0, MapSize);
        bottom = Math.Clamp(bottom, 0, MapSize);
        if (rightEdge <= left || bottom <= top)
            return;

        Rectangle rect = Rectangle.FromLTRB(left, top, rightEdge, bottom);
        map.Pending = map.Pending.IsEmpty ? rect : Rectangle.Union(map.Pending, rect);
    }

    /// <summary>
    /// The whole view of map <paramref name="index"/> for a box centred at <paramref name="box"/>, relative to
    /// <paramref name="anchor"/>.
    /// </summary>
    private GpuShadowView CreateView(int index, Vector3 box, Vector3 anchor)
    {
        Vector3 towards = _directions[index];
        Vector3 forward = -towards;
        Vector3 hint = MathF.Abs(forward.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(hint, forward));
        Vector3 up = Vector3.Cross(forward, right);

        // The square around the box's 8 corners as the direction sees them.
        float halfRegion = Region / 2;
        float half = halfRegion * (MathF.Abs(right.X) + MathF.Abs(right.Y) + MathF.Abs(right.Z));
        half = MathF.Max(half, halfRegion * (MathF.Abs(up.X) + MathF.Abs(up.Y) + MathF.Abs(up.Z)));
        float depthHalf = halfRegion * (MathF.Abs(forward.X) + MathF.Abs(forward.Y) + MathF.Abs(forward.Z));
        MapState state = _maps[index];
        state.View = new MapAxes(right, up, half);
        Rectangle rect = GetRect(index);

        return CreateOrthographic(towards, right, up, box - anchor, half, half, depthHalf, rect);
    }

    /// <summary>
    /// The view of the texels <paramref name="part"/> of a whole map's view: the same depth, across a smaller square.
    /// </summary>
    private GpuShadowView CreatePart(in GpuShadowView whole, Rectangle part)
    {
        // The whole view's axes and centre back from its matrix, whose first two columns are the axes over the half
        // sides.
        Matrix4x4 m = whole.ViewProjection;
        float halfX = 1 / new Vector3(m.M11, m.M21, m.M31).Length();
        float halfY = 1 / new Vector3(m.M12, m.M22, m.M32).Length();
        Vector3 right = new Vector3(m.M11, m.M21, m.M31) * halfX;
        Vector3 up = new Vector3(m.M12, m.M22, m.M32) * halfY;
        Vector3 center = (-m.M41 * halfX * right) + (-m.M42 * halfY * up);
        float texelX = 2 * halfX / whole.RectWidth;
        float texelY = 2 * halfY / whole.RectHeight;
        float partCenterX = -halfX + ((part.X + (part.Width / 2f)) * texelX);
        float partCenterY = halfY - ((part.Y + (part.Height / 2f)) * texelY);

        // The depth along the view stays the whole view's: the third column and its offset.
        Vector3 forwardOverRange = new(m.M13, m.M23, m.M33);
        float depthRange = whole.DepthRange;
        Vector3 partCenter = center + (right * partCenterX) + (up * partCenterY);
        Rectangle rect = new((int)whole.RectX + part.X, (int)whole.RectY + part.Y, part.Width, part.Height);
        GpuShadowView view = CreateOrthographic(
            whole.Towards,
            right,
            up,
            partCenter,
            part.Width * texelX / 2,
            part.Height * texelY / 2,
            0,
            rect
        );
        view.ViewProjection.M13 = forwardOverRange.X;
        view.ViewProjection.M23 = forwardOverRange.Y;
        view.ViewProjection.M33 = forwardOverRange.Z;
        view.ViewProjection.M43 = m.M43;
        view.DepthRange = depthRange;
        view.Plane5 = whole.Plane5;
        view.TexelSize = whole.TexelSize;

        return view;
    }

    /// <summary>
    /// An orthographic view towards the sky along <paramref name="towards"/>, of the square of half sides
    /// <paramref name="halfX"/> and <paramref name="halfY"/> around <paramref name="center"/>, with reversed depth from
    /// the near plane far towards the sky to <paramref name="depthHalf"/> past the centre, drawn into
    /// <paramref name="rect"/> of its atlas.
    /// </summary>
    private GpuShadowView CreateOrthographic(
        Vector3 towards,
        Vector3 right,
        Vector3 up,
        Vector3 center,
        float halfX,
        float halfY,
        float depthHalf,
        Rectangle rect
    )
    {
        Vector3 forward = -towards;
        float depthRange = CasterReach + depthHalf;
        Matrix4x4 viewProjection = new(
            right.X / halfX,
            up.X / halfY,
            -forward.X / depthRange,
            0,
            right.Y / halfX,
            up.Y / halfY,
            -forward.Y / depthRange,
            0,
            right.Z / halfX,
            up.Z / halfY,
            -forward.Z / depthRange,
            0,
            -Vector3.Dot(center, right) / halfX,
            -Vector3.Dot(center, up) / halfY,
            (depthHalf + Vector3.Dot(center, forward)) / depthRange,
            1
        );
        Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        return new GpuShadowView
        {
            ViewProjection = viewProjection,
            Plane0 = ToPlane(c3 + c0),
            Plane1 = ToPlane(c3 - c0),
            Plane2 = ToPlane(c3 + c1),
            Plane3 = ToPlane(c3 - c1),
            Plane4 = new Vector4(0, 0, 0, 1),
            Plane5 = ToPlane(c2),
            RectX = (uint)rect.X,
            RectY = (uint)rect.Y,
            RectWidth = (uint)rect.Width,
            RectHeight = (uint)rect.Height,
            AtlasTransform = new Vector4(
                (float)rect.Width / AtlasWidth,
                (float)rect.Height / AtlasHeight,
                (((2f * rect.X) + rect.Width) / AtlasWidth) - 1,
                1 - (((2f * rect.Y) + rect.Height) / AtlasHeight)
            ),
            Towards = towards,
            TexelSize = 2 * halfX / rect.Width,
            IsOrthographic = 1,
            DepthRange = depthRange,
            Casters = ShadowCasters.Still,
        };
    }

    /// <summary>
    /// A map's axes across its square, and the half side of the square.
    /// </summary>
    private readonly record struct MapAxes(Vector3 Right, Vector3 Up, float Half);

    /// <summary>
    /// What the fit keeps of one map from frame to frame.
    /// </summary>
    private sealed class MapState
    {
        public bool IsDrawn;

        public bool IsWholeThisFrame;

        /// <summary>
        /// The centre of the box the map holds.
        /// </summary>
        public Vector3 Box;

        public MapAxes View;

        /// <summary>
        /// The texels of the map that wait to be drawn again; empty while none do.
        /// </summary>
        public Rectangle Pending;
    }
}
