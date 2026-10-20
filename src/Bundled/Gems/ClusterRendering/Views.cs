using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
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
internal sealed class Views(IGpu gpu, IWindow? window, Assets assets, Threads threads, Scene scene, ClusterPool models, Materials materials, ClusterRenderingSettings settings, CancellationToken stopping) : IDisposable
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
        foreach (CameraCopy camera in frame.Copy.Cameras)
        {
            _seen.Add(camera.Entity);
            if (Target(camera.Camera.Target) is not { } target)
            {
                continue;
            }

            if (!_views.TryGetValue(camera.Entity, out View? view))
            {
                view = new View(gpu, scene, models, materials);
                _views[camera.Entity] = view;
            }

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
    public GpuTexture Window()
    {
        if (window is not null && _windowTarget == GpuTexture.None)
        {
            _windowTarget = gpu.WindowTarget(window);
        }

        return _windowTarget;
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
        Size size = image == GpuTexture.None ? Size.Empty : gpu.SizeOf(image);
        return size.IsEmpty ? null : new ViewTarget(image, target.Section.AreaIn(size), gpu.FormatOf(image), true);
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
            GpuTexture texture = gpu.CreateTexture(new GpuTextureDescription(GpuFormat.Rgba16Float, GpuTextureUsage.Sampled | GpuTextureUsage.ColorTarget, width, height));
            _renderTextures[handle] = BoundResource.Of(texture, width, height);
        });
    }
}

/// <summary>
/// What every view is worked out from this frame: the copy of the world, the frame, the sky's row, the frame's counts,
/// where the player is, and whether views jitter.
/// </summary>
internal readonly record struct ViewFrame(WorldCopy Copy, Frame Frame, int SkyMaterial, Counts Counts, Vector3? Player, bool Jitters);

/// <summary>
/// Where a view draws: a texture, the rectangle of it, its format, and whether it is a window's.
/// </summary>
internal readonly record struct ViewTarget(GpuTexture Texture, Rectangle Area, GpuFormat Format, bool IsWindow)
{
    /// <summary>
    /// How the view's image is encoded as it is shown: as it is into a render texture, which is linear, and into a
    /// window as its image's format expects, which is what the window's composition made it.
    /// </summary>
    public TargetEncoding Encoding => !IsWindow ? TargetEncoding.Linear
        : Format is GpuFormat.Rgba8Unorm or GpuFormat.Bgra8Unorm ? TargetEncoding.Srgb
        : Format == GpuFormat.Rgb10A2Unorm ? TargetEncoding.Pq
        : Format == GpuFormat.Rgba16Float ? TargetEncoding.ScRgb
        : TargetEncoding.Linear;
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
    private const int JitterPhases = 8;

    private readonly Scene _scene;
    private readonly ClusterPool _models;
    private readonly Materials _materials;
    private readonly GpuArray<Vector4> _exposure;
    private readonly GpuArray<PieceEntry> _transparentPieces;
    private readonly List<(int Slot, float Distance)> _transparentOrder = [];
    private Matrix4x4 _lastViewProj;
    private Vector3 _lastPosition;
    private bool _hasLast;

    public View(IGpu gpu, Scene scene, ClusterPool models, Materials materials)
    {
        _scene = scene;
        _models = models;
        _materials = materials;
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

    public FrameConstants Constants { get; private set; }

    public Counts Counts { get; private set; }

    /// <summary>
    /// Where the camera is.
    /// </summary>
    public Vector3 Position => Constants.CameraPosition;

    /// <summary>
    /// The view's transparent pieces, farthest instance first, as runs of one surface each.
    /// </summary>
    public List<TransparentRun> TransparentRuns { get; } = [];

    public GpuBuffer TransparentPieces => _transparentPieces.Buffer;

    /// <summary>
    /// Works out the frame for this view: its sizes, the targets drawn at the size it is drawn at, its constants, and its
    /// transparent pieces. The size it is shown at is its target's, which the post stage upscales to, unless the output
    /// filter is nearest: then it is the drawn size, and only the output pass scales, crisply.
    /// </summary>
    public void Prepare(CameraCopy camera, ViewTarget target, ClusterRenderingSettings settings, in ViewFrame frame)
    {
        Target = target;
        (uint width, uint height) = DrawnSize(target.Area.Width, target.Area.Height, settings.RenderWidth, settings.RenderHeight);
        // Nearest keeps the low resolution to the output pass, so the post stage runs at the drawn size, not the target's.
        Vector2 output = settings.OutputFilter == GpuFilter.Nearest ? new Vector2(width, height) : new Vector2(target.Area.Width, target.Area.Height);
        Make("Depth", GpuFormat.D32Float, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampled, width, height);
        Make("Visibility", GpuFormat.R32Uint, GpuTextureUsage.ColorTarget | GpuTextureUsage.GraphicsRead | GpuTextureUsage.ComputeRead, width, height);
        Make("Albedo", GpuFormat.Rgba8Srgb, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled, width, height);
        Make("Normal", GpuFormat.Rgb10A2Unorm, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled, width, height);
        Make("Material", GpuFormat.Rgba8Unorm, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled, width, height);
        Make("Hdr", GpuFormat.Rgba16Float, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled, width, height);
        Make("Velocity", GpuFormat.Rg16Float, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled, width, height);

        Camera lens = camera.Camera;
        Vector3 position = camera.Transform.Position;
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
            _lastPosition = position;
            _hasLast = true;
        }

        uint tilesX = (width + Views.TileSize - 1) / Views.TileSize;
        uint tilesY = (height + Views.TileSize - 1) / Views.TileSize;
        uint tileWords = Math.Max(1, (frame.Counts.Lights + 31) / 32);
        float binScale = Views.ZBinCount / MathF.Log2(Views.ZBinFar / lens.Near);
        uint resolveTiles = (width + Views.ResolveTile - 1) / Views.ResolveTile * ((height + Views.ResolveTile - 1) / Views.ResolveTile);
        (uint hizTexels, uint hizLevels) = HiZOf(width, height);
        Counts = frame.Counts with { Tiles = resolveTiles, HizTexels = hizTexels, HizLevels = hizLevels, LightTileWords = tilesX * tilesY * tileWords };
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
            SunDirection = frame.Copy.Sun?.Direction ?? Vector3.UnitZ,
            SunAngularRadius = 0.00465f,
            SunIlluminance = frame.Copy.Sun?.Illuminance ?? Vector3.Zero,
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
            CameraMotion = position - _lastPosition,
            PixelScale = height * 0.5f / tanY,
            PriorityPosition = (frame.Player ?? position) - position,
            TextureMipScale = frame.Jitters ? MathF.Min(1, height / MathF.Max(1, output.Y)) : 1,
            Jitter = jitter,
            OutputSize = output,
        };

        _lastViewProj = view * still;
        _lastPosition = position;
        SortTransparent();
        _exposure.Flush();
        _transparentPieces.Flush();
        Resources.Set("Exposure", BoundResource.Of(_exposure.Buffer, _exposure.Bytes));
    }

    /// <summary>
    /// The size a view is drawn at: its target's, or the width and height the settings ask for, one of them alone
    /// keeping the target's proportions; a pixel at least, and four times the target each way at most.
    /// </summary>
    private static (uint Width, uint Height) DrawnSize(float targetWidth, float targetHeight, int wantedWidth, int wantedHeight)
    {
        float proportion = targetHeight > 0 ? targetWidth / targetHeight : 1;
        float width = wantedWidth > 0 ? wantedWidth : wantedHeight > 0 ? wantedHeight * proportion : targetWidth;
        float height = wantedHeight > 0 ? wantedHeight : wantedWidth > 0 ? wantedWidth / proportion : targetHeight;
        return ((uint)Math.Clamp(MathF.Ceiling(width), 1, Math.Max(1, MathF.Ceiling(targetWidth * 4))), (uint)Math.Clamp(MathF.Ceiling(height), 1, Math.Max(1, MathF.Ceiling(targetHeight * 4))));
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
    private void SortTransparent()
    {
        TransparentRuns.Clear();
        _transparentOrder.Clear();
        Vector3 position = Position;
        foreach (int slot in _scene.Transparent)
        {
            _transparentOrder.Add((slot, -Vector3.Distance(_scene.PositionOf(slot), position)));
        }

        CollectionsMarshal.AsSpan(_transparentOrder).Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int count = 0;
        foreach ((int slot, _) in _transparentOrder)
        {
            Renderer renderer = _scene.RendererOf(slot);
            if (_models.InPool(renderer.Model) is not ({ } firstCluster, { } packed) || packed.Levels.Length == 0)
            {
                continue;
            }

            GpuLevel finest = packed.Levels[0];
            for (int i = (int)finest.FirstCluster; i < finest.FirstCluster + finest.Count; i++)
            {
                GpuCluster cluster = packed.Clusters[i];
                if (_materials.Get(renderer.Materials[(int)((cluster.Counts >> 17) & 7)]) is not { Type: MaterialType.Transparent } drawn)
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
