using System.Buffers;
using System.Diagnostics;
using Magic.Assets;
using Magic.Debugging;
using Magic.Platform;
using Magic.SdlBindings;
using Magic.Threading;
using Magic.Utils;
using static Magic.SdlBindings.SDL_PixelFormat;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlPlatform;

/// <summary>
/// The window through SDL3; there is none when headless.
/// </summary>
/// <remarks>
/// <para>
/// The platform thread shows what the game thread sets on it.
/// </para>
/// <para>
/// It also paces the frames: <see cref="Update"/> waits out the frame cap of its settings. One cap applies while the
/// window has the focus, and another while it does not.
/// </para>
/// </remarks>
public sealed class Window : IWindow
{
    private readonly SdlWindow _sdl;

    private readonly SdlPlatformSettings _settings;

    private readonly App _app;

    private readonly Threads _threads;

    private readonly AssetManager _assets;

    private string _title;

    /// <summary>
    /// What the window was last given, to show a change of the settings.
    /// </summary>
    private Placement _shown;

    /// <summary>
    /// When the last frame the frame cap held back was let go, 0 before the first.
    /// </summary>
    private long _pacedTimestamp;

    internal unsafe Window(
        SdlWindow sdl,
        SdlPlatformSettings settings,
        string title,
        App app,
        Threads threads,
        AssetManager assets
    )
    {
        _sdl = sdl;
        _settings = settings;
        _title = title;
        _app = app;
        _threads = threads;
        _assets = assets;
        _shown = new Placement(settings.Mode, settings.Display, settings.IsResizable);
        if (sdl.Pointer == null)
            return;

        Handle = ReadHandle();
        Icon = assets.GetDefault<Icon>();
    }

    public string Title
    {
        get => _title;
        set
        {
            SdlWindow.ThrowIfNotOnGame(_threads);
            _title = value;
            Volatile.Write(ref _sdl.TitleToShow, value);
        }
    }

    /// <summary>
    /// The icon that the window shows.
    /// </summary>
    /// <remarks>
    /// Set it only on the game thread. It reaches the window after it and its image load, unless another icon was set
    /// meanwhile.
    /// </remarks>
    public unsafe Handle<Icon> Icon
    {
        get;
        set
        {
            SdlWindow.ThrowIfNotOnGame(_threads);
            field = value;
            if (_sdl.Pointer != null)
                ShowIconAsync(value).FireAndForget();
        }
    }

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Width => Volatile.Read(ref _sdl.Width);

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Height => Volatile.Read(ref _sdl.Height);

    /// <summary>
    /// 1 when headless.
    /// </summary>
    public float DisplayScale => Volatile.Read(ref _sdl.DisplayScale);

    public WindowHandle Handle { get; }

    /// <summary>
    /// Shows a changed window mode, display or resizability on the platform thread, then waits out the frame cap.
    /// </summary>
    /// <remarks>
    /// Call it only on the game thread.
    /// </remarks>
    internal void Update()
    {
        Placement settings = new(_settings.Mode, _settings.Display, _settings.IsResizable);
        if (settings != _shown && !_app.IsHeadless)
            ShowWindowAsync(_shown, settings).FireAndForget();

        _shown = settings;

        // The frame is held back until a frame at the cap's rate is due after the last one held back, so frames start no
        // faster than the cap; a frame already late is let go at once, and the next is due a whole frame after it.
        int framesPerSecond = _sdl.IsFocused ? _settings.MaxFrameRate : _settings.BackgroundFrameRate;
        if (framesPerSecond <= 0)
            return;

        long now = Stopwatch.GetTimestamp();
        long due = _pacedTimestamp + (Stopwatch.Frequency / framesPerSecond);
        if (due > now)
            SDL_DelayPrecise((ulong)Stopwatch.GetElapsedTime(now, due).Ticks * 100);

        _pacedTimestamp = Math.Max(due, now);
    }

    /// <summary>
    /// Gets the named pointer in a window's <paramref name="properties"/>; 0 when there is none.
    /// </summary>
    private static unsafe nint GetPointer(uint properties, ReadOnlySpan<byte> name)
    {
        fixed (byte* key = name)
        {
            return (nint)SDL_GetPointerProperty(properties, key, null);
        }
    }

    /// <summary>
    /// Loads <paramref name="icon"/> and its image on workers, then gives the image's first level to the window on the
    /// platform thread.
    /// </summary>
    /// <remarks>
    /// SDL copies the image. It does nothing if the run ended or another icon was set meanwhile. If the icon does not
    /// load or is compressed, it logs a warning, and the window keeps its icon.
    /// </remarks>
    private async Task ShowIconAsync(Handle<Icon> icon)
    {
        if (icon == Handle<Icon>.None)
            return;

        CancellationToken lifetime = _app.Lifetime;
        Result<Icon> loaded = await _assets.LoadAsync(icon, null, lifetime).ConfigureAwait(false);
        Result<Texture> image = loaded.Ok
            ? await _assets.LoadAsync(loaded.Value.Image, null, lifetime).ConfigureAwait(false)
            : Result.Failure(loaded.Error);
        if (!image.Ok)
        {
            Log.Warn($"The window's icon is not shown: {image.Error}");

            return;
        }

        await _threads.Enter(EngineThread.Platform);
        if (lifetime.IsCancellationRequested || Icon != icon)
            return;

        Texture texture = image.Value;
        if (texture.Format is not (TextureFormat.Rgba8Srgb or TextureFormat.Rgba8Unorm))
        {
            Log.Warn($"The window's icon is not shown: its image is {texture.Format}, not uncompressed.");

            return;
        }

        TextureLevel top = texture.Levels[0];
        using MemoryHandle pixels = texture.Pixels.Pin();
        unsafe
        {
            SDL_Surface* surface = SDL_CreateSurfaceFrom(
                top.Width,
                top.Height,
                SDL_PIXELFORMAT_RGBA32,
                pixels.Pointer,
                top.Width * 4
            );
            if (surface == null || !SDL_SetWindowIcon(_sdl.Pointer, surface))
                Log.Warn($"The window's icon is not shown: {Sdl.Error}");

            SDL_DestroySurface(surface);
        }
    }

    /// <summary>
    /// Gives the window the display, mode and resizability <paramref name="settings"/> change from
    /// <paramref name="old"/>, on the platform thread, unless the run ended meanwhile.
    /// </summary>
    private async Task ShowWindowAsync(Placement old, Placement settings)
    {
        CancellationToken lifetime = _app.Lifetime;
        await _threads.Enter(EngineThread.Platform);
        if (lifetime.IsCancellationRequested)
            return;

        // It moves to another display as a window, as one fullscreen or maximized can't be moved: once it is a window,
        // which some systems finish later, centred there, then back in its mode.
        bool isMoving = settings.Display != old.Display;
        unsafe
        {
            if (isMoving)
            {
                SetMode(WindowMode.Windowed);
                SDL_SyncWindow(_sdl.Pointer);
                int centered = (int)(SDL_WINDOWPOS_CENTERED_MASK | SdlWindow.FindDisplay(settings.Display));
                SDL_SetWindowPosition(_sdl.Pointer, centered, centered);
            }

            if (isMoving || settings.Mode != old.Mode)
                SetMode(settings.Mode);

            if (settings.IsResizable != old.IsResizable)
                SDL_SetWindowResizable(_sdl.Pointer, settings.IsResizable);
        }
    }

    /// <summary>
    /// Sets the window's mode.
    /// </summary>
    /// <remarks>
    /// Fullscreen fills the display without a border. Maximized and windowed leave fullscreen first, because a
    /// fullscreen window neither maximizes nor restores. Windowed returns the window to its last size.
    /// </remarks>
    private unsafe void SetMode(WindowMode mode)
    {
        SDL_SetWindowFullscreen(_sdl.Pointer, mode == WindowMode.Fullscreen);
        if (mode == WindowMode.Maximized)
            SDL_MaximizeWindow(_sdl.Pointer);
        else if (mode == WindowMode.Windowed)
            SDL_RestoreWindow(_sdl.Pointer);
    }

    /// <summary>
    /// Reads what the OS knows the window by, from SDL's properties.
    /// </summary>
    private unsafe WindowHandle ReadHandle()
    {
        if (OperatingSystem.IsMacOS())
            return new WindowHandle(WindowSystem.Cocoa, 0, (nint)SDL_Metal_GetLayer(_sdl.MetalView));

        uint properties = SDL_GetWindowProperties(_sdl.Pointer);
        if (OperatingSystem.IsWindows())
        {
            return new WindowHandle(
                WindowSystem.Windows,
                GetPointer(properties, SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER),
                GetPointer(properties, SDL_PROP_WINDOW_WIN32_HWND_POINTER)
            );
        }

        // Linux runs on either, whichever SDL found.
        nint wayland = GetPointer(properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER);
        if (wayland != 0)
            return new WindowHandle(
                WindowSystem.Wayland,
                wayland,
                GetPointer(properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER)
            );

        fixed (byte* window = SDL_PROP_WINDOW_X11_WINDOW_NUMBER)
        {
            return new WindowHandle(
                WindowSystem.X11,
                GetPointer(properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER),
                (nint)SDL_GetNumberProperty(properties, window, 0)
            );
        }
    }

    /// <summary>
    /// The window's mode, display and resizability, as its settings give them.
    /// </summary>
    private readonly record struct Placement(WindowMode Mode, int Display, bool IsResizable);
}
