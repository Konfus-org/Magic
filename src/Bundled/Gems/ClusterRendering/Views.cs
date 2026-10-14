using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Every camera's view: where it draws (a section of the window or a render texture), its own visibility buffer,
/// G-buffer, scene colour, velocity and exposure, kept while its camera is, and its constants and transparent pieces
/// worked out each frame from the <see cref="Snapshot"/>. Render thread.
/// </summary>
internal sealed class Views(IGpu gpu, IWindow? window, Assets assets, Threads threads, ClusterRenderingSettings settings, CancellationToken stopping) : IDisposable
{
    /// <summary>
    /// Pixels a side of a light tile, which Shaders/Lights/LightTiles.comp.hlsl bins the lights into.
    /// </summary>
    public const int TileSize = 32;

    /// <summary>
    /// Pixels a side of a tile the G-buffer is shaded by, a resolve bin's tiles each one quad.
    /// </summary>
    public const int ResolveTile = 8;

    /// <summary>
    /// Z-bins a view's depth is cut into, evenly in its logarithm out to <see cref="ZBinFar"/> metres.
    /// </summary>
    public const int ZBinCount = 1024;

    public const float ZBinFar = 20000;

    private readonly Dictionary<Entity, View> _views = [];
    private readonly Dictionary<Handle<RenderTexture>, BoundResource> _renderTextures = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<View> _drawn = [];
    private GpuTexture _windowTarget;

    public void Dispose()
    {
        foreach (View view in _views.Values)
        {
            view.Dispose();
        }

        foreach (BoundResource texture in _renderTextures.Values)
        {
            gpu.Release(texture.Texture);
        }

        if (_windowTarget != GpuTexture.None)
        {
            gpu.Release(_windowTarget);
        }
    }

    /// <summary>
    /// The views this frame draws: each camera whose target has an image, its constants and transparent pieces worked
    /// out; a view whose camera is gone is let go.
    /// </summary>
    public List<View> Prepare(in ViewFrame frame)
    {
        _drawn.Clear();
        _seen.Clear();
        foreach (CameraCopy camera in frame.Snapshot.Cameras)
        {
            _seen.Add(camera.Entity);
            if (Target(camera.Camera.Target) is not { } target)
            {
                continue;
            }

            if (!_views.TryGetValue(camera.Entity, out View? view))
            {
                view = new View(gpu);
                _views[camera.Entity] = view;
            }

            view.Prepare(camera, target, settings, frame);
            _drawn.Add(view);
        }

        foreach (Entity gone in new List<Entity>(_views.Keys))
        {
            if (!_seen.Contains(gone))
            {
                _views.Remove(gone, out View? view);
                view?.Dispose();
            }
        }

        return _drawn;
    }

    /// <summary>
    /// Where a camera draws this frame: its section of the window, or its render texture, whole; none while the window
    /// has no image or the texture loads.
    /// </summary>
    private ViewTarget? Target(CameraTarget target)
    {
        if (!target.IsWindow)
        {
            if (_renderTextures.TryGetValue(target.Texture, out BoundResource texture))
            {
                return texture.Texture == GpuTexture.None ? null : new ViewTarget(texture.Texture, new Rectangle(0, 0, (int)texture.Width, (int)texture.Height), GpuFormat.Rgba16Float, false);
            }

            _renderTextures[target.Texture] = default;
            LoadAsync(target.Texture).FireAndForget();
            return null;
        }

        GpuTexture image = Window();
        Size size = image == GpuTexture.None ? Size.Empty : gpu.Size(image);
        return size.IsEmpty ? null : new ViewTarget(image, target.Section.AreaIn(size), gpu.Format(image), true);
    }

    /// <summary>
    /// The window's image, which a camera draws into its section of; none without a window.
    /// </summary>
    public GpuTexture Window()
    {
        if (window is not null && _windowTarget == GpuTexture.None)
        {
            _windowTarget = gpu.Target(window);
        }

        return _windowTarget;
    }

    private async Task LoadAsync(Handle<RenderTexture> handle)
    {
        Result<RenderTexture> loaded = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
        if (!loaded.Ok)
        {
            Debugging.Error($"A camera's render texture does not load, so it draws nothing: {loaded.Error}");
            return;
        }

        uint width = (uint)Math.Max(1, loaded.Value.Width);
        uint height = (uint)Math.Max(1, loaded.Value.Height);
        threads.Post(EngineThread.Render, () =>
        {
            GpuTexture texture = gpu.CreateTexture(new GpuTextureDesc(GpuFormat.Rgba16Float, GpuTextureUsage.Sampler | GpuTextureUsage.ColorTarget, width, height));
            _renderTextures[handle] = BoundResource.Of(texture, width, height);
        });
    }
}

/// <summary>
/// What every view is worked out from this frame: the copy of the world, the frame, the scene and its models and
/// materials, the sky's row, the frame's counts, where the player is, and whether views jitter.
/// </summary>
internal readonly record struct ViewFrame(Snapshot Snapshot, Frame Frame, Scene Scene, ClusterPool Models, Materials Materials, int SkyMaterial, Counts Counts, Vector3D? Player, bool Jitters);

/// <summary>
/// Where a view draws: a texture, the rectangle of it, its format, and whether it is a window's.
/// </summary>
internal readonly record struct ViewTarget(GpuTexture Texture, Rectangle Area, GpuFormat Format, bool IsWindow);

/// <summary>
/// One camera's view: its resources, kept from frame to frame, the last frame's view-projection and camera position,
/// which velocity is worked out from, and what this frame works out for it.
/// </summary>
internal sealed class View : IDisposable
{
    /// <summary>
    /// The sub-pixel offsets a jittering view cycles through, Halton's (2, 3) sequence, which covers a pixel evenly.
    /// </summary>
    private const int JitterPhases = 8;

    private readonly GpuArray<Vector4> _exposure;
    private readonly GpuArray<PieceEntry> _transparentPieces;
    private readonly List<(int Slot, float Distance)> _transparentOrder = [];
    private Matrix4x4 _lastViewProj;
    private Vector3D _lastPosition;
    private bool _hasLast;

    public View(IGpu gpu)
    {
        Resources = new ResourceSet(gpu);
        _exposure = new GpuArray<Vector4>(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite, 1);
        _exposure.Set(0, new Vector4(1, 0, 0, 0));
        _transparentPieces = new GpuArray<PieceEntry>(gpu, GpuBufferUsage.Vertex, 64);
    }

    public void Dispose()
    {
        Resources.Dispose();
        _exposure.Dispose();
        _transparentPieces.Dispose();
    }

    public ResourceSet Resources { get; }

    public ViewTarget Target { get; private set; }

    public bool TargetIsWindow => Target.IsWindow;

    public FrameConstants Constants { get; private set; }

    public Counts Counts { get; private set; }

    /// <summary>
    /// Where the camera is, in doubles.
    /// </summary>
    public Vector3D Position { get; private set; }

    /// <summary>
    /// The view's transparent pieces, farthest instance first, as runs of one surface each.
    /// </summary>
    public List<TransparentRun> TransparentRuns { get; } = [];

    public GpuBuffer TransparentPieces => _transparentPieces.Buffer;

    /// <summary>
    /// Works out the frame for this view: its sizes, the targets drawn at the size it is drawn at, its constants, and its
    /// transparent pieces.
    /// </summary>
    public void Prepare(CameraCopy camera, ViewTarget target, ClusterRenderingSettings settings, in ViewFrame frame)
    {
        Target = target;
        float scale = Math.Clamp(settings.RenderScale, 0.1f, 2);
        uint width = (uint)Math.Max(1, MathF.Ceiling(target.Area.Width * scale));
        uint height = (uint)Math.Max(1, MathF.Ceiling(target.Area.Height * scale));
        Make("Depth", GpuFormat.D32Float, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler, width, height);
        Make("Visibility", GpuFormat.R32Uint, GpuTextureUsage.ColorTarget | GpuTextureUsage.GraphicsRead | GpuTextureUsage.ComputeRead, width, height);
        Make("Albedo", GpuFormat.Rgba8Srgb, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler, width, height);
        Make("Normal", GpuFormat.Rgb10A2Unorm, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler, width, height);
        Make("Material", GpuFormat.Rgba8Unorm, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler, width, height);
        Make("Hdr", GpuFormat.Rgba16Float, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler, width, height);
        Make("Velocity", GpuFormat.Rg16Float, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler, width, height);

        Camera lens = camera.Camera;
        Position = camera.Transform.Position;
        Vector3 high = Position.ToVector3();
        float tanY = MathF.Tan(lens.FieldOfView * (MathF.PI / 360));
        float tanX = tanY * width / height;
        Vector2 jitter = frame.Jitters ? JitterOf(frame.Frame.Index, width, height) : Vector2.Zero;
        Matrix4x4 view = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(camera.Transform.Rotation));
        Matrix4x4 projection = new(1 / tanX, 0, 0, 0, 0, 1 / tanY, 0, 0, jitter.X, jitter.Y, 0, 1, 0, 0, lens.Near, 0);
        Matrix4x4 still = projection with { M31 = 0, M32 = 0 };
        Matrix4x4 viewProj = view * projection;
        if (!_hasLast)
        {
            _lastViewProj = view * still;
            _lastPosition = Position;
            _hasLast = true;
        }

        uint tilesX = (width + Views.TileSize - 1) / Views.TileSize;
        uint tilesY = (height + Views.TileSize - 1) / Views.TileSize;
        uint tileWords = Math.Max(1, (frame.Counts.Lights + 31) / 32);
        float binScale = Views.ZBinCount / MathF.Log2(Views.ZBinFar / lens.Near);
        uint resolveTiles = (width + Views.ResolveTile - 1) / Views.ResolveTile * ((height + Views.ResolveTile - 1) / Views.ResolveTile);
        (uint hizTexels, uint hizLevels) = HiZOf(width, height);
        Counts = frame.Counts with { Tiles = resolveTiles, HizTexels = hizTexels, HizLevels = hizLevels, LightTiles = tilesX * tilesY, LightTileWords = tilesX * tilesY * tileWords };
        Constants = new FrameConstants
        {
            ViewProj = viewProj,
            View = view,
            CameraPosition = high,
            Time = (float)frame.Frame.Seconds,
            ViewSize = new Vector2(width, height),
            ViewTexel = new Vector2(1f / width, 1f / height),
            ProjScale = new Vector2(1 / tanX, 1 / tanY),
            Near = lens.Near,
            Far = lens.Far,
            SunDirection = frame.Snapshot.Sun?.Direction ?? Vector3.UnitZ,
            SunAngularRadius = 0.00465f,
            SunIlluminance = frame.Snapshot.Sun?.Illuminance ?? Vector3.Zero,
            LightCount = frame.Counts.Lights,
            FrameIndex = (uint)(frame.Frame.Index % uint.MaxValue),
            TileCountX = tilesX,
            TileWords = tileWords,
            ZBinScale = binScale,
            ZBinBias = -MathF.Log2(lens.Near) * binScale,
            ZBinCount = Views.ZBinCount,
            DeltaSeconds = frame.Frame.DeltaSeconds,
            TargetIsWindow = target.IsWindow ? 1u : 0u,
            SkyMaterial = (uint)frame.SkyMaterial,
            PrevViewProj = _lastViewProj,
            CameraLow = (Position - high).ToVector3(),
            ClusterError = Math.Clamp(settings.ClusterError, 0.25f, 16),
            CameraMotion = (Position - _lastPosition).ToVector3(),
            LodScale = height * 0.5f / tanY,
            PriorityPosition = ((frame.Player ?? Position) - Position).ToVector3(),
            TextureLodScale = frame.Jitters ? MathF.Min(1, scale) : 1,
            Jitter = jitter,
            OutputSize = new Vector2(target.Area.Width, target.Area.Height),
        };

        _lastViewProj = view * still;
        _lastPosition = Position;
        SortTransparent(frame);
        _exposure.Flush();
        _transparentPieces.Flush();
        Resources.Set("Exposure", BoundResource.Of(_exposure.Buffer, _exposure.Bytes));
    }

    /// <summary>
    /// The texels of a view's depth pyramid, the depth halved again and again down to one texel, the farthest of each
    /// four kept, and how many levels it has.
    /// </summary>
    private static (uint Texels, uint Levels) HiZOf(uint width, uint height)
    {
        uint texels = 0;
        uint levels = 0;
        (uint w, uint h) = (Math.Max(1, (width + 1) / 2), Math.Max(1, (height + 1) / 2));
        while (true)
        {
            texels += w * h;
            levels++;
            if (w == 1 && h == 1)
            {
                return (texels, levels);
            }

            (w, h) = (Math.Max(1, (w + 1) / 2), Math.Max(1, (h + 1) / 2));
        }
    }

    /// <summary>
    /// This frame's sub-pixel offset in clip space, a phase of Halton's (2, 3) sequence, a pixel wide at most.
    /// </summary>
    private static Vector2 JitterOf(ulong frame, uint width, uint height)
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

    private void Make(string name, GpuFormat format, GpuTextureUsage usage, uint width, uint height)
    {
        Resources.Make(name, new ResourceKey(false, format, GpuTextureKind.Texture2D, usage, GpuBufferUsage.None, width, height, 1, 1, 0));
    }

    /// <summary>
    /// The scene's transparent instances, farthest first, each its finest clusters drawn with a transparent material, a
    /// piece of up to 32 triangles at a time, as runs of one surface each.
    /// </summary>
    private void SortTransparent(in ViewFrame frame)
    {
        TransparentRuns.Clear();
        _transparentOrder.Clear();
        foreach (int slot in frame.Scene.Transparent)
        {
            _transparentOrder.Add((slot, -(float)Vector3D.Distance(frame.Scene.PositionOf(slot), Position)));
        }

        CollectionsMarshal.AsSpan(_transparentOrder).Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int count = 0;
        foreach ((int slot, _) in _transparentOrder)
        {
            Renderer renderer = frame.Scene.RendererOf(slot);
            if (frame.Models.InPool(renderer.Model) is not ({ } firstCluster, { } packed) || packed.Levels.Length == 0)
            {
                continue;
            }

            GpuLevel finest = packed.Levels[0];
            for (int i = (int)finest.FirstCluster; i < finest.FirstCluster + finest.Count; i++)
            {
                GpuCluster cluster = packed.Clusters[i];
                if (frame.Materials.Get(Scene.MaterialIn(renderer.Materials, (int)((cluster.Counts >> 17) & 7))) is not { Type: MaterialType.Transparent } drawn)
                {
                    continue;
                }

                uint triangles = cluster.Counts & 0xFF;
                for (uint piece = 0; piece * 32 < triangles; piece++)
                {
                    _transparentPieces.Set(count, new PieceEntry((uint)slot, ((uint)(firstCluster + i) << 2) | piece, 0));
                    if (TransparentRuns.Count > 0 && TransparentRuns[^1].Surface == drawn.Surface && TransparentRuns[^1].First + TransparentRuns[^1].Count == count)
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

/// <summary>
/// One piece of up to 32 triangles to draw: its instance's slot, its cluster's row times four plus which piece of it,
/// or a card's row with the top bit, and its slot among the view's pieces, which the visibility buffer names it by.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PieceEntry(uint Instance, uint What, uint Slot);
