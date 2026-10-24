using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.VoxelRendering;

/// <summary>
/// What the frame's constants and every view's are worked out from: the copy of the world, the frame, where the player
/// is, and what the world's constants name (<see cref="WorldIds"/>).
/// </summary>
internal readonly record struct ViewFrame(
    Renderables Copy,
    Frame Frame,
    Vector3? Player,
    WorldIds World);

/// <summary>
/// What the world's constants name (<see cref="WorldConstants"/>): its tables, its geometry, the sampler materials are
/// read with and the frame's raised failure codes, the sky's material row, and the sky's harmonics.
/// </summary>
internal readonly record struct WorldIds(
    GpuBuffer Instances,
    GpuBuffer Models,
    GpuBuffer Levels,
    GpuBuffer Clusters,
    GpuBuffer Cards,
    GpuBuffer Materials,
    GpuBuffer MaterialBins,
    GpuBuffer Geometry,
    GpuSampler MaterialSampler,
    GpuBuffer Failures,
    uint SkyMaterial,
    GpuBuffer SkySh);

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
/// The frame's constants, which every pass reads, and every camera's view drawn into the window: where it draws, its
/// section of the window, and its constants. Each worked out each frame from the <see cref="WorldCopy"/>'s copy into a
/// buffer of its own, which a pass's header names (<see cref="PassHeader"/>). A view whose camera draws into a render
/// texture is not drawn yet. Render thread.
/// </summary>
internal sealed class Views : IDisposable
{
    private readonly IGpuDriver _gpu;
    private readonly IWindow? _window;
    private readonly VoxelRenderingSettings _settings;
    private readonly Dictionary<Entity, View> _views = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<View> _drawn = [];
    private GpuTexture _windowImage;

    internal Views(IGpuDriver gpu, IWindow? window, VoxelRenderingSettings settings)
    {
        _gpu = gpu;
        _window = window;
        _settings = settings;
        World = gpu.CreateBuffer(
            GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead,
            (uint)Unsafe.SizeOf<WorldConstants>());
    }

    /// <summary>
    /// The buffer of this frame's <see cref="WorldConstants"/>.
    /// </summary>
    internal GpuBuffer World { get; }

    public void Dispose()
    {
        foreach (View view in _views.Values)
        {
            view.Dispose();
        }

        _gpu.Release(World);
        _gpu.Release(_windowImage);
    }

    /// <summary>
    /// The frame's constants, sent, and the views this frame draws: each camera whose target has an image, its
    /// constants worked out and sent; a view whose camera is gone is let go.
    /// </summary>
    internal List<View> Prepare(in ViewFrame frame)
    {
        WorldConstants world = GetWorld(frame);
        _gpu.Upload(World, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<WorldConstants>(in world)));
        _drawn.Clear();
        _seen.Clear();
        foreach (CameraCopy camera in frame.Copy.Cameras)
        {
            _seen.Add(camera.Entity);
            if (!_views.TryGetValue(camera.Entity, out View? view))
            {
                view = new View(_gpu, camera.Entity.Id);
                _views[camera.Entity] = view;
            }

            if (GetTarget(camera.Camera.Target) is not { } target) continue;

            view.Prepare(camera, target, _settings, frame);
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
    internal GpuTexture GetWindow()
    {
        if (_window is not null && _windowImage == GpuTexture.None) _windowImage = _gpu.GetWindowTarget(_window);

        return _windowImage;
    }

    /// <summary>
    /// The world's constants as the frame holds them: its clocks, its lights, weather and sky, and what it names.
    /// </summary>
    private static WorldConstants GetWorld(in ViewFrame frame)
    {
        Renderables copy = frame.Copy;
        RenderEffects<IWeatherEffect> weather = copy.Weather?.Effects ?? default;
        weather.TryGet(out Wind wind);
        weather.TryGet(out Clouds clouds);
        WorldIds ids = frame.World;

        return new WorldConstants
        {
            Clocks = new FrameConstants(frame.Frame),
            Lights = GetLights(copy),
            Instances = ids.Instances,
            Models = ids.Models,
            Levels = ids.Levels,
            Clusters = ids.Clusters,
            Cards = ids.Cards,
            Materials = ids.Materials,
            MaterialBins = ids.MaterialBins,
            Geometry = ids.Geometry,
            SkyMaterial = ids.SkyMaterial,
            MaterialSampler = ids.MaterialSampler,
            Failures = ids.Failures,
            SkySh = ids.SkySh,
            Weather = new WeatherConstants
            {
                Wind = wind.Velocity,
                Coverage = Math.Clamp(clouds.Coverage, 0, 1),
                CloudColor = new Vector3(clouds.Color.R, clouds.Color.G, clouds.Color.B),
                Density = Math.Clamp(clouds.Density, 0, 1),
                CloudShadow = new Vector3(clouds.Shadow.R, clouds.Shadow.G, clouds.Shadow.B),
            },
            Celestial = GetCelestial(copy),
        };
    }

    /// <summary>
    /// The lights the frame holds: its key light, none without one, and the other directional lights, as many as fit.
    /// </summary>
    private static WorldLights GetLights(Renderables copy)
    {
        WorldLights lights = new()
        {
            Key = new DirectionalLightConstants
            {
                Direction = copy.KeyLightDirection,
                AngularRadius = copy.KeyLight?.AngularRadius ?? 0,
                Illuminance = copy.KeyLight?.Illuminance ?? Vector3.Zero,
            },
            DirectionalCount = (uint)Math.Min(copy.Directional.Count, DirectionalLights.Capacity),
        };
        for (int i = 0; i < (int)lights.DirectionalCount; i++)
        {
            lights.Directional[i] = new DirectionalLightConstants
            {
                Direction = copy.Directional[i].Direction,
                Illuminance = copy.Directional[i].Illuminance,
            };
        }

        return lights;
    }

    /// <summary>
    /// The sky's sun and moon as the frame holds them, each pointing straight down and giving no light without one.
    /// </summary>
    private static CelestialConstants GetCelestial(Renderables copy) => new()
    {
        SunDirection = copy.Sun?.Direction ?? -Vector3.UnitY,
        SunAngularRadius = copy.Sun?.AngularRadius ?? 0,
        SunIlluminance = copy.Sun?.Illuminance ?? Vector3.Zero,
        MoonDirection = copy.Moon?.Direction ?? -Vector3.UnitY,
        MoonAngularRadius = copy.Moon?.AngularRadius ?? 0,
        MoonIlluminance = copy.Moon?.Illuminance ?? Vector3.Zero,
    };

    /// <summary>
    /// Where a camera draws this frame: its section of the window; none while the window has no image, and none for a
    /// render texture.
    /// </summary>
    private ViewTarget? GetTarget(CameraTarget target)
    {
        if (!target.IsWindow) return null;

        GpuTexture image = GetWindow();
        Size size = image == GpuTexture.None ? Size.Empty : _gpu.GetSize(image);

        return size.IsEmpty
            ? null
            : new ViewTarget(image, target.Section.GetAreaIn(size), _gpu.GetFormat(image), true);
    }
}

/// <summary>
/// One camera's view: its constants' buffer and its exposure, kept from frame to frame, the last frame's
/// view-projection and camera position, which velocity is worked out from, and what this frame works out for it.
/// </summary>
internal sealed class View : IDisposable
{
    /// <summary>
    /// A view's exposure until an exposure effect meters it: 1, the light as it is, not yet metered, as
    /// Shaders/Post/Exposure.slang keeps it.
    /// </summary>
    private static readonly float[] _unmetered = [1, 0, 0, 0];

    private readonly IGpuDriver _gpu;
    private Matrix4x4 _lastViewProj;
    private Vector3 _lastPosition;
    private bool _hasLast;

    internal View(IGpuDriver gpu, int id)
    {
        _gpu = gpu;
        Id = id;
        Constants = gpu.CreateBuffer(GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, ConstantBytes);
        Exposure = gpu.CreateBuffer(
            GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite,
            sizeof(float) * 4);
        gpu.Upload(Exposure, 0, MemoryMarshal.AsBytes(_unmetered.AsSpan()));
    }

    internal int Id { get; }

    /// <summary>
    /// The buffer of this frame's constants, which its shaders read.
    /// </summary>
    internal GpuBuffer Constants { get; }

    /// <summary>
    /// The view's exposure, a row of four floats: the exposure, its EV100, and whether it was ever metered.
    /// </summary>
    internal GpuBuffer Exposure { get; }

    internal ViewTarget Target { get; private set; }

    /// <summary>
    /// The view as the pipeline script sees it this frame.
    /// </summary>
    internal RenderView Render { get; private set; } = new(0, Size.Empty, Size.Empty, GpuBuffer.None, GpuBuffer.None);

    private static uint ConstantBytes => (uint)Unsafe.SizeOf<ViewConstants>();

    public void Dispose()
    {
        _gpu.Release(Constants);
        _gpu.Release(Exposure);
    }

    /// <summary>
    /// Works out the frame for this view: its sizes and its constants, sent to its buffer. The size it is shown at is
    /// its target's part.
    /// </summary>
    internal void Prepare(CameraCopy camera, ViewTarget target, VoxelRenderingSettings settings, in ViewFrame frame)
    {
        Target = target;
        (uint width, uint height) = GetDrawnSize(target.Area.Width, target.Area.Height, settings);
        Camera lens = camera.Camera;
        Vector3 position = camera.Transform.Position;
        float tanY = MathF.Tan(lens.FieldOfView * (MathF.PI / 360));
        float tanX = tanY * width / height;
        Matrix4x4 view = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(camera.Transform.Rotation));
        Matrix4x4 projection = new(1 / tanX, 0, 0, 0, 0, 1 / tanY, 0, 0, 0, 0, 0, 1, 0, 0, lens.Near, 0);
        Matrix4x4 viewProj = view * projection;
        if (!_hasLast)
        {
            _lastViewProj = viewProj;
            _lastPosition = position;
            _hasLast = true;
        }

        ViewConstants values = new()
        {
            ViewProj = viewProj,
            Rotation = view,
            PrevViewProj = _lastViewProj,
            CameraPosition = position,
            ClusterError = Math.Clamp(settings.ClusterError, 0.25f, 16),
            ViewSize = new Vector2(width, height),
            ViewTexel = new Vector2(1f / width, 1f / height),
            ProjScale = new Vector2(1 / tanX, 1 / tanY),
            Near = lens.Near,
            Far = lens.Far,
            OutputSize = new Vector2(target.Area.Width, target.Area.Height),
            CameraMotion = position - _lastPosition,
            PixelScale = height * 0.5f / tanY,
            PriorityPosition = (frame.Player ?? position) - position,
            TextureMipScale = 1,
            TargetEncoding = (uint)target.Encoding,
            Exposure = Exposure,
        };
        _gpu.Upload(Constants, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<ViewConstants>(in values)));
        Render = new RenderView(
            Id,
            new Size((int)width, (int)height),
            new Size(target.Area.Width, target.Area.Height),
            Constants,
            Exposure);
        _lastViewProj = viewProj;
        _lastPosition = position;
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
        VoxelRenderingSettings settings)
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
}
