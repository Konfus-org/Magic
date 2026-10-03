using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using SDL3;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SDLWindowingGem;

/// <summary>
/// Creates SDL windows and answers who is open. Factory and registry live in one class on purpose: there
/// is exactly one window table, and the class that adds to it (Create) and removes from it (close events,
/// Dispose) is the only one that can keep it truthful. A separate registry would either duplicate the
/// table or need callbacks from the factory to stay in sync; neither buys anything. It publishes
/// <see cref="EventType.FocusGained"/> and <see cref="EventType.FocusLost"/>; keyboard, text and mouse are the input
/// gem's. Text input is on for every window, so <see cref="EventType.TextInput"/> always arrives.
/// <para>
/// The OS ties a window to the thread that made it, which is the render thread (<see cref="ThreadId.Render"/>): the
/// events arrive there, from the SDL gem's pump. A window can still be used from the main thread, by a script say:
/// whatever changes it is run on the render thread and waited for, and its sizes are kept as the events report
/// them, so reading one waits for nothing.
/// </para>
/// </summary>
internal sealed class WindowManager : IGem, IWindowFactory, IWindowRegistry
{
    private readonly Assets _assets;
    private readonly Events _events;
    private readonly Threads _threads;
    private readonly Handle<Texture> _projectIcon;

    // Windows by handle (the SDL window id): that is what window events carry. A window stays here after
    // it is closed, until it is disposed: closing hides it, disposing destroys it (see OnCloseRequested).
    private readonly Dictionary<uint, Window> _byHandle = [];

    // The same windows in creation order, so Main is "the first one still open" rather than dictionary luck.
    private readonly List<Window> _ordered = [];

    // Kept in a field: SDL holds the native function pointer, and a collected delegate would crash the next event.
    private readonly SDL.EventFilter _watch;

    public WindowManager(Project project, Assets assets, Events events, Threads threads)
    {
        _assets = assets;
        _events = events;
        _threads = threads;
        _projectIcon = project.Icon;

        // The SDL gem owns SDL_Init/SDL_Quit and the event pump; this one only adds the subsystem it uses and
        // watches the queue. SDL runs the watch for every event as it is queued, from the pump on the render thread.
        if (!SDL.InitSubSystem(SDL.InitFlags.Video))
            throw new InvalidOperationException($"SDL video initialization failed: {SDL.GetError()}");

        Debugging.Log.Verbose($"SDL video initialised on thread {Environment.CurrentManagedThreadId}.");

        _watch = OnEvent;
        SDL.AddEventWatch(_watch, IntPtr.Zero);
    }

    public void Dispose()
    {
        SDL.RemoveEventWatch(_watch, IntPtr.Zero);
        CloseAll();
        SDL.QuitSubSystem(SDL.InitFlags.Video);
    }

    public IWindow? Main
    {
        get
        {
            foreach (Window window in _ordered)
            {
                if (window.IsOpen)
                    return window;
            }

            return null;
        }
    }

    // A fresh list each call: callers iterate it while windows close underneath them.
    public IReadOnlyList<IWindow> Windows => [.. _ordered.Where(window => window.IsOpen)];

    public IWindow? Get(uint handle)
    {
        return _byHandle.TryGetValue(handle, out Window? window) && window.IsOpen ? window : null;
    }

    public IWindow Create(string title, int width, int height, WindowMode mode)
    {
        Window newWindow = _threads.Invoke(ThreadId.Render, () => new Window(title, width, height, mode, _assets, _threads, _projectIcon));

        _byHandle[newWindow.Handle] = newWindow;
        _ordered.Add(newWindow);

        return newWindow;
    }

    // A close request must not destroy the SDL_Window*: a GPU device may have claimed it, and that claim is
    // only released when the render gem unloads, which happens after the host sees IsOpen go false. So the
    // window is marked closed and hidden here, and destroyed by whoever owns it (Dispose) or by CloseAll.
    private void OnCloseRequested(uint handle)
    {
        if (_byHandle.TryGetValue(handle, out Window? window))
            window.Close();
    }

    private void CloseAll()
    {
        foreach (Window window in _ordered)
            window.Dispose();

        _ordered.Clear();
        _byHandle.Clear();
    }

    /// <summary>
    /// Sees every event as it is queued; always lets it through for whoever else watches.
    /// </summary>
    private bool OnEvent(IntPtr userdata, ref SDL.Event e)
    {
        switch ((SDL.EventType)e.Type)
        {
            case SDL.EventType.WindowCloseRequested:
                OnCloseRequested(e.Window.WindowID);
                break;
            case SDL.EventType.Quit:
                foreach (Window window in _ordered)
                    window.Close();
                break;
            case SDL.EventType.WindowResized:
            case SDL.EventType.WindowPixelSizeChanged:
                if (_byHandle.TryGetValue(e.Window.WindowID, out Window? resized))
                    resized.ReadSizes();
                break;
            case SDL.EventType.WindowFocusGained:
            case SDL.EventType.WindowFocusLost:
                EventType focus = (SDL.EventType)e.Type == SDL.EventType.WindowFocusGained ? EventType.FocusGained : EventType.FocusLost;
                _events.Publish(new Event(focus, e.Window.WindowID));
                break;
        }

        return true;
    }

    private sealed class Window : IWindow
    {
        private readonly Assets _assets;
        private readonly Threads _threads;

        // The SDL_Window* stays private; the host only ever sees the id.
        private nint _window;
        private Size _size;
        private CancellationTokenSource? _iconCancel; // stops the icon on its way when another is set or the window closes
        private Task<Texture?>? _iconLoad; // the icon's texture on its way, or there; null once shown, or when there is none

        /// <summary>
        /// On the render thread.
        /// </summary>
        public Window(string title, int width, int height, WindowMode mode, Assets assets, Threads threads, Handle<Texture> icon)
        {
            _assets = assets;
            _threads = threads;

            _window = SDL.CreateWindow(title, width, height, SDL.WindowFlags.Hidden | SDL.WindowFlags.Resizable);
            if (_window == IntPtr.Zero)
                throw new InvalidOperationException($"SDL window creation failed: {SDL.GetError()}");

            Handle = SDL.GetWindowID(_window);
            IsOpen = true;
            SDL.StartTextInput(_window);

            Title = title;
            Size = new Size(width, height);
            Mode = mode;
            Icon = icon;
            ReadSizes();
        }

        public void Dispose()
        {
            if (_window == IntPtr.Zero)
                return;

            IsOpen = false;
            OnRenderThread(() => SDL.DestroyWindow(_window));
            _window = IntPtr.Zero;
        }

        /// <summary>
        /// False once closed; the SDL window itself lives on until <see cref="Dispose"/>.
        /// </summary>
        public bool IsOpen { get; private set; }

        public uint Handle { get; }

        public string Title
        {
            get;
            set
            {
                field = value;
                OnRenderThread(() => SDL.SetWindowTitle(_window, value));
            }
        }

        /// <summary>
        /// The texture shown as the window's icon; <see cref="Handle{T}.None"/> leaves the OS default. The texture
        /// is loaded without anyone waiting for it, and its top mip level handed to SDL, which keeps its own copy:
        /// on a window already showing, the icon changes a moment after it is set. <see cref="Show"/> waits for it,
        /// so a window never appears without its icon: the taskbar takes the icon a window has when it appears.
        /// </summary>
        public Handle<Texture> Icon
        {
            get;
            set
            {
                field = value;
                OnRenderThread(() =>
                {
                    StopIconLoad();
                    if (!value.IsValid)
                        return;

                    _iconCancel = new CancellationTokenSource();
                    _iconLoad = LoadIconAsync(value, _iconCancel.Token);
                    ShowIconWhenLoadedAsync(_iconLoad).FireAndForget();
                });
            }
        }

        /// <summary>
        /// As SDL last reported it: the user resizes the window too.
        /// </summary>
        public Size Size
        {
            get => _size;
            set => Change(() => SDL.SetWindowSize(_window, value.Width, value.Height));
        }

        public Size PixelSize { get; private set; }

        public WindowMode Mode
        {
            get;
            set
            {
                field = value;
                switch (value)
                {
                    case WindowMode.Windowed:
                        Change(() =>
                        {
                            SDL.SetWindowFullscreen(_window, false);
                            SDL.SetWindowBordered(_window, true);
                        });
                        break;
                    case WindowMode.Fullscreen:
                        Change(() =>
                        {
                            SDL.SetWindowBordered(_window, true);
                            SDL.SetWindowFullscreen(_window, true);
                        });
                        break;
                    case WindowMode.Borderless:
                        Change(() =>
                        {
                            SDL.SetWindowFullscreen(_window, false);
                            SDL.SetWindowBordered(_window, false);
                        });
                        break;
                }
            }
        }

        public void Show()
        {
            Change(() =>
            {
                ShowLoadedIcon();
                SDL.ShowWindow(_window);
            });
        }

        public void Hide()
        {
            OnRenderThread(() => SDL.HideWindow(_window));
        }

        public void Minimize()
        {
            Change(() => SDL.MinimizeWindow(_window));
        }

        public void Maximize()
        {
            Change(() => SDL.MaximizeWindow(_window));
        }

        /// <summary>
        /// Marks the window closed and hides it, keeping the SDL window for whoever still holds it (a GPU device). On the render thread.
        /// </summary>
        public void Close()
        {
            IsOpen = false;
            StopIconLoad();

            if (_window != IntPtr.Zero)
                SDL.HideWindow(_window);
        }

        /// <summary>
        /// Takes the window's sizes from SDL: when it is made, when it was changed here, and when an event says the user changed it. On the render thread.
        /// </summary>
        public void ReadSizes()
        {
            _size = SDL.GetWindowSize(_window, out int width, out int height) ? new Size(width, height) : Size.Empty;
            PixelSize = SDL.GetWindowSizeInPixels(_window, out int pixelWidth, out int pixelHeight) ? new Size(pixelWidth, pixelHeight) : Size.Empty;
        }

        /// <summary>
        /// Stops the icon on its way, if one is: its load ends without a word. On the render thread.
        /// </summary>
        private void StopIconLoad()
        {
            _iconCancel?.Cancel();
            _iconCancel?.Dispose();
            _iconCancel = null;
            _iconLoad = null;
        }

        /// <summary>
        /// Loads the icon's texture on the workers: null when it cannot be one, which is logged, or when
        /// <paramref name="cancel"/> stops it, which is not. Never faults.
        /// </summary>
        private async Task<Texture?> LoadIconAsync(Handle<Texture> handle, CancellationToken cancel)
        {
            try
            {
                Texture? icon = await _assets.LoadAsync(handle, cancel: cancel).ConfigureAwait(false);
                if (icon is null)
                    return null; // the manager logged why

                if (icon.Format is not (TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb) || icon.Levels.Length == 0)
                {
                    Debugging.Log.Warn($"{icon.Path} cannot be a window icon: it is {icon.Format}, and an icon must be uncompressed RGBA.");
                    return null;
                }

                return icon;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.Log.Warn($"The window icon could not be loaded: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Hands the icon to the window on the render thread once it is loaded, for a window already showing.
        /// </summary>
        private async Task ShowIconWhenLoadedAsync(Task<Texture?> load)
        {
            await load.ConfigureAwait(false);
            _threads.Post(ThreadId.Render, ShowLoadedIcon);
        }

        /// <summary>
        /// Hands the icon on its way to the window, waiting for it when it is not there yet; nothing when there is
        /// none, when it was shown already, or when another was set meanwhile. On the render thread.
        /// </summary>
        private void ShowLoadedIcon()
        {
            Task<Texture?>? load = _iconLoad;
            if (load is null || _window == IntPtr.Zero)
                return;

            Texture? icon = load.GetAwaiter().GetResult(); // never faults, and the load needs nothing of this thread
            _iconLoad = null;
            if (icon is not null)
                ShowIcon(icon);
        }

        /// <summary>
        /// On the render thread.
        /// </summary>
        private void ShowIcon(Texture icon)
        {
            // CreateSurfaceFrom wraps the pixels in place, so they stay pinned until SDL has taken its copy.
            TextureLevel level = icon.Levels[0];
            GCHandle pin = GCHandle.Alloc(icon.Pixels, GCHandleType.Pinned);
            try
            {
                nint surface = SDL.CreateSurfaceFrom(level.Width, level.Height, SDL.PixelFormat.ABGR8888, pin.AddrOfPinnedObject() + level.Offset, level.Width * 4);
                if (surface == IntPtr.Zero)
                {
                    Debugging.Log.Warn($"Could not wrap {icon.Path} as a surface: {SDL.GetError()}");
                    return;
                }

                if (!SDL.SetWindowIcon(_window, surface))
                    Debugging.Log.Warn($"Could not set {icon.Path} as the window icon: {SDL.GetError()}");
                else if (OperatingSystem.IsWindows())
                    Win32.CopyIconToClass(_window);
                SDL.DestroySurface(surface);
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>
        /// Runs an SDL window call on the thread the window belongs to, and waits for it.
        /// </summary>
        private void OnRenderThread(Action work)
        {
            _threads.Invoke(ThreadId.Render, work);
        }

        /// <summary>
        /// A call that may change the window's size: the sizes are read again once it has run.
        /// </summary>
        private void Change(Action work)
        {
            OnRenderThread(() =>
            {
                work();
                ReadSizes();
            });
        }
    }
}

/// <summary>
/// What SDL leaves out on Windows. Explorer asks a window for its icon, with a short timeout, as it makes the
/// window's taskbar button; a thread busy with the GPU right after <see cref="IWindow.Show"/> misses the question,
/// and Explorer falls back to the window class's icon, which SDL never sets, then to the exe's, which there is none
/// of: a blank button. The class icon needs no answer, so it is set to the same icon.
/// </summary>
internal static class Win32
{
    private const uint WM_GETICON = 0x7F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int GCLP_HICONSM = -34;
    private const int GCLP_HICON = -14;

    /// <summary>
    /// Sets the class icons of <paramref name="window"/>'s class (every SDL window shares it) to the icons SDL gave the
    /// window. On the window's thread.
    /// </summary>
    public static void CopyIconToClass(nint window)
    {
        nint hwnd = SDL.GetPointerProperty(SDL.GetWindowProperties(window), SDL.Props.WindowWin32HWNDPointer, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            return;

        nint big = SendMessageW(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero);
        if (big != IntPtr.Zero)
            SetClassLongPtrW(hwnd, GCLP_HICON, big);

        nint small = SendMessageW(hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero);
        if (small != IntPtr.Zero)
            SetClassLongPtrW(hwnd, GCLP_HICONSM, small);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetClassLongPtrW(nint hwnd, int index, nint value);
}
