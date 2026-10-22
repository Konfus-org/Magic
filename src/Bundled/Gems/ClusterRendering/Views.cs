using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// What every view is worked out from this frame: the copy of the world, the frame, the sky's row, the frame's counts,
/// where the player is, whether views jitter, and the weather's fog falloff once it is in.
/// </summary>
internal readonly record struct ViewFrame(
    Renderables Copy,
    Frame Frame,
    int SkyMaterial,
    Counts Counts,
    Vector3? Player,
    bool Jitters,
    Curve? FogFalloff);

/// <summary>
/// Where a view draws: a texture, the rectangle of it, its format, and whether it is a window's.
/// </summary>
internal readonly record struct ViewTarget(GpuTexture Texture, Rectangle Area, GpuFormat Format, bool IsWindow)
{
    /// <summary>
    /// How the view's image is encoded as it is shown: as it is into a render texture, which is linear, and into a
    /// window as its image's format expects, which is what the window's composition made it.
    /// </summary>
    internal TargetEncoding Encoding => (IsWindow, Format) switch
    {
        (true, GpuFormat.Rgba8Unorm or GpuFormat.Bgra8Unorm) => TargetEncoding.Srgb,
        (true, GpuFormat.Rgb10A2Unorm) => TargetEncoding.Pq,
        (true, GpuFormat.Rgba16Float) => TargetEncoding.ScRgb,
        _ => TargetEncoding.Linear,
    };
}

/// <summary>
/// Every camera's view: where it draws (a section of the window or a render texture, which the view holds while
/// it draws into it), its own visibility buffer, G-buffer, scene colour, velocity and exposure, kept while
/// its camera is, and its constants and transparent pieces worked out each frame from the
/// <see cref="WorldProcessor"/>'s copy. Render thread.
/// </summary>
internal sealed class Views(
    IGpu gpu,
    IWindow? window,
    GpuResources resources,
    Instances instances,
    ClusterRenderingSettings settings) : IDisposable
{
    /// <summary>
    /// Pixels a side of a light tile, which Shaders/Lights/LightTiles.comp.hlsl bins the lights into.
    /// </summary>
    internal const int TileSize = 32;

    /// <summary>
    /// Pixels a side of a tile the G-buffer is shaded by, a resolve bin's tiles each one quad.
    /// </summary>
    internal const int ResolveTile = 8;

    /// <summary>
    /// Z-bins a view's depth is cut into, evenly in its logarithm out to <see cref="ZBinFar"/> metres.
    /// </summary>
    internal const int ZBinCount = 1024;

    internal const float ZBinFar = 20000;

    private readonly Dictionary<Entity, View> _views = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<View> _drawn = [];

    public void Dispose()
    {
        foreach (View view in _views.Values)
        {
            view.Dispose();
        }
    }

    /// <summary>
    /// The views this frame draws: each camera whose target has an image, its constants and transparent pieces worked
    /// out; a view whose camera is gone is let go, with its render texture.
    /// </summary>
    internal List<View> Prepare(in ViewFrame frame)
    {
        _drawn.Clear();
        _seen.Clear();
        foreach (CameraCopy camera in frame.Copy.Cameras)
        {
            _seen.Add(camera.Entity);
            if (!_views.TryGetValue(camera.Entity, out View? view))
            {
                view = new View(resources, instances);
                _views[camera.Entity] = view;
            }

            view.Hold(camera.Camera.Target);

            if (GetTarget(camera.Camera.Target) is not { } target) continue;

            view.Prepare(camera, target, settings, frame);
            _drawn.Add(view);
        }

        foreach ((Entity entity, View view) in _views)
        {
            if (!_seen.Contains(entity))
            {
                _views.Remove(entity);
                view.Dispose();
            }
        }

        return _drawn;
    }

    /// <summary>
    /// The window's image, which a camera draws into its section of; none without a window.
    /// </summary>
    internal GpuTexture GetWindow() => resources.GetWindowTarget(window);

    /// <summary>
    /// Where a camera draws this frame: its section of the window, or its render texture, whole; none while the window
    /// has no image or the texture loads.
    /// </summary>
    private ViewTarget? GetTarget(CameraTarget target)
    {
        if (!target.IsWindow)
        {
            BoundResource texture = resources.GetTexture(target.Texture);

            return texture.Texture == GpuTexture.None ? null : new ViewTarget(
                texture.Texture,
                new Rectangle(0, 0, (int)texture.Width, (int)texture.Height),
                GpuFormat.Rgba16Float,
                false);
        }

        GpuTexture image = GetWindow();
        Size size = image == GpuTexture.None ? Size.Empty : gpu.GetSize(image);

        return size.IsEmpty ? null : new ViewTarget(image, target.Section.AreaIn(size), gpu.GetFormat(image), true);
    }
}

/// <summary>
/// One camera's view: its resources, kept from frame to frame, the last frame's view-projection and camera position,
/// which velocity is worked out from, and what this frame works out for it.
/// </summary>
internal sealed class View : IDisposable
{
    /// <summary>
    /// The sub-pixel offsets a jittering view cycles through, Halton's (2, 3) sequence, which covers a pixel evenly.
    /// </summary>
    internal const int JitterPhases = 8;

    private readonly GpuResources _resources;
    private readonly Instances _instances;
    private readonly GpuTable<Vector4> _exposure;
    private readonly GpuTable<GpuPiece> _transparentPieces;
    private readonly List<(int Slot, float Distance)> _transparentOrder = [];
    private Matrix4x4 _lastViewProj;
    private Vector3 _lastPosition;
    private bool _hasLast;

    /// <summary>
    /// The render texture the view draws into, held while it does; none for the window.
    /// </summary>
    private Handle<RenderTexture> _held;

    internal View(GpuResources resources, Instances instances)
    {
        _resources = resources;
        _instances = instances;
        Resources = resources.MakeScope();
        _exposure = resources.MakeTable<Vector4>(GpuContract.Exposure, 1, Resources);

        // Exposure 1, not metered yet: its first metering is taken whole (Post/ExposureAdapt).
        _exposure.Set(0, new Vector4(1, 0, 0, 0));
        _transparentPieces = resources.MakeTable<GpuPiece>(GpuBufferUsage.Vertex, 64, Resources);
    }

    /// <summary>
    /// The view's own scope: its targets, its exposure and what passes make for it, found before the frame's.
    /// </summary>
    internal ResourceScope Resources { get; }

    internal ViewTarget Target { get; private set; }

    internal FrameConstants Constants { get; private set; }

    internal Counts Counts { get; private set; }

    /// <summary>
    /// Where the camera is.
    /// </summary>
    internal Vector3 Position => Constants.CameraPosition;

    /// <summary>
    /// The view's transparent pieces, farthest instance first, as runs of one surface each.
    /// </summary>
    internal List<TransparentRun> TransparentRuns { get; } = [];

    internal GpuBuffer TransparentPieces => _transparentPieces.Buffer;

    public void Dispose()
    {
        Hold(default);
        _resources.Release(Resources);
    }

    /// <summary>
    /// Holds the render texture <paramref name="target"/> names, in place of the one held before; none for the window.
    /// </summary>
    internal void Hold(CameraTarget target)
    {
        Handle<RenderTexture> texture = target.IsWindow ? default : target.Texture;

        if (texture == _held) return;

        if (texture != Handle<RenderTexture>.None) _resources.Record(texture);

        if (_held != Handle<RenderTexture>.None) _resources.Release(_held);

        _held = texture;
    }

    /// <summary>
    /// Works out the frame for this view: its sizes, the targets drawn at the size it is drawn at, its constants, and
    /// its transparent pieces. The size it is shown at is its target's, which the post stage upscales to, unless the
    /// settings turn the upscale off: then it is the drawn size, and only the output pass scales, crisply.
    /// </summary>
    internal void Prepare(CameraCopy camera, ViewTarget target, ClusterRenderingSettings settings, in ViewFrame frame)
    {
        Target = target;
        (uint width, uint height) = GetDrawnSize(target.Area.Width, target.Area.Height, settings);
        // Without the upscale the low resolution is kept to the output pass, so the post stage runs at the drawn
        // size, not the target's.
        Vector2 output = settings.Upscale
            ? new Vector2(target.Area.Width, target.Area.Height)
            : new Vector2(width, height);
        Make(GpuContract.Depth, width, height);
        Make(GpuContract.Visibility, width, height);
        Make(GpuContract.Albedo, width, height);
        Make(GpuContract.Normal, width, height);
        Make(GpuContract.Material, width, height);
        Make(GpuContract.Hdr, width, height);
        Make(GpuContract.Velocity, width, height);

        Camera lens = camera.Camera;
        Vector3 position = camera.Transform.Position;
        float tanY = MathF.Tan(lens.FieldOfView * (MathF.PI / 360));
        float tanX = tanY * width / height;
        Vector2 jitter = frame.Jitters ? GetJitter(frame.Frame.Index, width, height) : Vector2.Zero;
        Matrix4x4 view = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(camera.Transform.Rotation));
        Matrix4x4 projection = new(1 / tanX, 0, 0, 0, 0, 1 / tanY, 0, 0, jitter.X, jitter.Y, 0, 1, 0, 0, lens.Near, 0);
        Matrix4x4 still = projection with { M31 = 0, M32 = 0 };
        Matrix4x4 viewProj = view * projection;
        if (!_hasLast)
        {
            _lastViewProj = view * still;
            _lastPosition = position;
            _hasLast = true;
        }

        uint tilesX = (width + Views.TileSize - 1) / Views.TileSize;
        uint tilesY = (height + Views.TileSize - 1) / Views.TileSize;
        uint tileWords = Math.Max(1, (frame.Counts.Lights + 31) / 32);
        float binScale = Views.ZBinCount / MathF.Log2(Views.ZBinFar / lens.Near);
        uint resolveTiles = (width + Views.ResolveTile - 1) / Views.ResolveTile
            * ((height + Views.ResolveTile - 1) / Views.ResolveTile);
        (uint hizTexels, uint hizLevels) = GetHiZSize(width, height);
        Counts = frame.Counts with
        {
            Tiles = resolveTiles,
            HizTexels = hizTexels,
            HizLevels =
            hizLevels,
            LightTileWords = tilesX * tilesY * tileWords
        };
        Weather weather = frame.Copy.Weather ?? new Weather();

        // The fog is left out until its falloff is in, so it never shows first without its shape.
        CurveRows falloff = default;
        Span<float> falloffFloats = falloff;
        ParamLayout.WriteCurve(frame.FogFalloff, MemoryMarshal.AsBytes(falloffFloats));
        Constants = new FrameConstants
        {
            ViewProj = viewProj,
            View = view,
            CameraPosition = position,
            Time = (float)frame.Frame.Seconds,
            ViewSize = new Vector2(width, height),
            ViewTexel = new Vector2(1f / width, 1f / height),
            ProjScale = new Vector2(1 / tanX, 1 / tanY),
            Near = lens.Near,
            Far = lens.Far,
            KeyLightDirection = frame.Copy.KeyLightDirection,
            KeyLightAngularRadius = frame.Copy.KeyLight?.AngularRadius ?? 0,
            KeyLightIlluminance = frame.Copy.KeyLight?.Illuminance ?? Vector3.Zero,
            LightCount = frame.Counts.Lights,
            FrameIndex = (uint)(frame.Frame.Index % uint.MaxValue),
            TileCountX = tilesX,
            TileWords = tileWords,
            ZBinScale = binScale,
            ZBinBias = -MathF.Log2(lens.Near) * binScale,
            ZBinCount = Views.ZBinCount,
            DeltaSeconds = frame.Frame.DeltaSeconds,
            TargetEncoding = (uint)target.Encoding,
            SkyMaterial = (uint)frame.SkyMaterial,
            PrevViewProj = _lastViewProj,
            ClusterError = Math.Clamp(settings.ClusterError, 0.25f, 16),
            KeyLightThroughSky = frame.Copy.KeyLight is { IsSkyLight: true } ? 1u : 0u,
            DirectionalCount = (uint)Math.Min(frame.Copy.Directional.Count, DirectionalLights.Capacity),
            CameraMotion = position - _lastPosition,
            PixelScale = height * 0.5f / tanY,
            PriorityPosition = (frame.Player ?? position) - position,
            TextureMipScale = frame.Jitters ? MathF.Min(1, height / MathF.Max(1, output.Y)) : 1,
            Jitter = jitter,
            OutputSize = output,
            Weather = new WeatherConstants
            {
                Wind = weather.Wind,
                Coverage = Math.Clamp(weather.Coverage, 0, 1),
                CloudColor = new Vector3(weather.CloudColor.R, weather.CloudColor.G, weather.CloudColor.B),
                Density = Math.Clamp(weather.Density, 0, 1),
                CloudShadow = new Vector3(weather.CloudShadow.R, weather.CloudShadow.G, weather.CloudShadow.B),
                Fog = frame.FogFalloff is null ? 0 : MathF.Max(weather.Fog, 0),
                FogColor = new Vector3(weather.FogColor.R, weather.FogColor.G, weather.FogColor.B),
                FogStart = MathF.Max(weather.FogStart, 0),
                FogFalloff = falloff,
            },
            SkyLights = GetSkyLights(frame.Copy),
            Directional = GetDirectionalLights(frame.Copy.Directional),
        };

        _lastViewProj = view * still;
        _lastPosition = position;
        SortTransparent();
    }

    /// <summary>
    /// The size a view is drawn at: the width and height the settings name, one of them alone keeping the target's
    /// proportions, or else the target's times the render scale; a pixel at least, and four times the target each way
    /// at most. Rounded to the nearest pixel, not up, as a share of a size worked out in floats can land a hair past a
    /// whole one.
    /// </summary>
    private static (uint Width, uint Height) GetDrawnSize(
        float targetWidth,
        float targetHeight,
        ClusterRenderingSettings settings)
    {
        float proportion = targetHeight > 0 ? targetWidth / targetHeight : 1;
        float scale = Math.Clamp(settings.RenderScale, 0.1f, 2);
        (float Width, float Height) wanted = (settings.RenderWidth, settings.RenderHeight) switch
        {
            ( > 0, > 0) => (settings.RenderWidth, settings.RenderHeight),
            ( > 0, _) => (settings.RenderWidth, settings.RenderWidth / proportion),
            (_, > 0) => (settings.RenderHeight * proportion, settings.RenderHeight),
            _ => (targetWidth * scale, targetHeight * scale),
        };

        return (ToPixels(wanted.Width, targetWidth), ToPixels(wanted.Height, targetHeight));

        static uint ToPixels(float size, float target)
            => (uint)Math.Clamp(MathF.Round(size), 1, Math.Max(1, MathF.Ceiling(target * 4)));
    }

    /// <summary>
    /// The sky's sun and moon as the frame holds them, each pointing straight down and giving no light without one.
    /// </summary>
    private static SkyLightConstants GetSkyLights(Renderables copy) => new()
    {
        SunDirection = copy.Sun?.Direction ?? -Vector3.UnitY,
        SunAngularRadius = copy.Sun?.AngularRadius ?? 0,
        SunIlluminance = copy.Sun?.Illuminance ?? Vector3.Zero,
        MoonDirection = copy.Moon?.Direction ?? -Vector3.UnitY,
        MoonAngularRadius = copy.Moon?.AngularRadius ?? 0,
        MoonIlluminance = copy.Moon?.Illuminance ?? Vector3.Zero,
    };

    /// <summary>
    /// The directional lights that light the world alone, as many as the frame holds.
    /// </summary>
    private static DirectionalLights GetDirectionalLights(List<DirectionalCopy> lights)
    {
        DirectionalLights held = default;
        for (int i = 0; i < Math.Min(lights.Count, DirectionalLights.Capacity); i++)
        {
            held[i] = new DirectionalLightConstants
            {
                Direction = lights[i].Direction,
                Illuminance = lights[i].Illuminance,
            };
        }

        return held;
    }

    /// <summary>
    /// The texels of a view's depth pyramid, the depth halved again and again down to one texel, the farthest of each
    /// four kept, and how many levels it has.
    /// </summary>
    private static (uint Texels, uint Levels) GetHiZSize(uint width, uint height)
    {
        uint texels = 0;
        uint levels = 0;
        (uint w, uint h) = (Math.Max(1, (width + 1) / 2), Math.Max(1, (height + 1) / 2));
        while (true)
        {
            texels += w * h;
            levels++;

            if (w == 1 && h == 1) return (texels, levels);

            (w, h) = (Math.Max(1, (w + 1) / 2), Math.Max(1, (h + 1) / 2));
        }
    }

    /// <summary>
    /// This frame's sub-pixel offset in clip space, a phase of Halton's (2, 3) sequence, a pixel wide at most.
    /// </summary>
    private static Vector2 GetJitter(ulong frame, uint width, uint height)
    {
        int phase = (int)(frame % JitterPhases) + 1;

        return new Vector2((Halton(phase, 2) - 0.5f) * 2 / width, (Halton(phase, 3) - 0.5f) * 2 / height);

        static float Halton(int index, int radix)
        {
            float result = 0;
            float fraction = 1f / radix;
            while (index > 0)
            {
                result += index % radix * fraction;
                index /= radix;
                fraction /= radix;
            }

            return result;
        }
    }

    /// <summary>
    /// One of the view's own targets, in the format and use its contract entry has, made again when its size changes.
    /// </summary>
    private void Make(ResourceInfo info, uint width, uint height)
    {
        Resources.MakeIfChanged(
            info.Name,
            new ResourceKey(
                false,
                info.Format,
                GpuTextureKind.Texture2D,
                info.TextureUsage,
                GpuBufferUsage.None,
                width,
                height,
                1,
                1,
                0));
    }

    /// <summary>
    /// The world's transparent instances, farthest first, each its finest clusters drawn with a transparent material, a
    /// piece of up to 32 triangles at a time, as runs of one surface each.
    /// </summary>
    private void SortTransparent()
    {
        TransparentRuns.Clear();
        _transparentOrder.Clear();
        Vector3 position = Position;
        foreach (int slot in _instances.Transparent)
        {
            _transparentOrder.Add((slot, -Vector3.Distance(_instances.GetPosition(slot), position)));
        }

        CollectionsMarshal.AsSpan(_transparentOrder)
            .Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int count = 0;
        foreach ((int slot, _) in _transparentOrder)
        {
            Renderer renderer = _instances.GetRenderer(slot);

            if (_resources.FindInPool(renderer.Model) is not ({ } firstCluster, { } packed)
                || packed.Levels.Length == 0)
            {
                continue;
            }

            GpuLevel finest = packed.Levels[0];
            for (int i = (int)finest.FirstCluster; i < finest.FirstCluster + finest.Count; i++)
            {
                GpuCluster cluster = packed.Clusters[i];

                if (_resources.Get(renderer.Materials[(int)((cluster.Counts >> 17) & 7)])
                    is not { Type: MaterialType.Transparent } drawn)
                {
                    continue;
                }

                uint triangles = cluster.Counts & 0xFF;
                for (uint piece = 0; piece * 32 < triangles; piece++)
                {
                    _transparentPieces.Set(count, new GpuPiece((uint)slot, ((uint)(firstCluster + i) << 2) | piece, 0));
                    if (TransparentRuns.Count > 0 && TransparentRuns[^1].Surface == drawn.Surface
                        && TransparentRuns[^1].First + TransparentRuns[^1].Count == count)
                    {
                        TransparentRuns[^1] = TransparentRuns[^1] with { Count = TransparentRuns[^1].Count + 1 };
                    }
                    else
                    {
                        TransparentRuns.Add(new TransparentRun(drawn.Surface, count, 1));
                    }

                    count++;
                }
            }
        }
    }
}
