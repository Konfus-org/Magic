using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Contexts.Input;
using Magic.Contexts.Threading;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using SDL3;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SdlPlatformGem;

/// <summary>
/// The windows and the clipboard. The registry is one window table: what adds to it (<see cref="Create"/>) and
/// closes or destroys its windows (close events, <see cref="CloseAll"/>) is the one place that keeps it truthful. It publishes
/// <see cref="WindowFocus"/>, and <see cref="Quit"/> when the main window is closed. Text input is on for every
/// window, so <see cref="TextInput"/> always arrives. A window can be used from the main thread, by a script say:
/// whatever changes it is run on the render thread and waited for, and its sizes are kept as the events report them,
/// so reading one waits for nothing.
/// </summary>
internal sealed partial class SdlPlatform : IWindowRegistry, IClipboard
{
    // Windows by handle (the SDL window id): that is what window events carry. A window stays here after
    // it is closed, until the gem unloads: closing hides it, CloseAll destroys it (see OnCloseRequested).
    private readonly Dictionary<uint, Window> _byHandle = [];

    // The same windows in creation order, so Main is "the first one still open" rather than dictionary luck.
    private readonly List<Window> _ordered = [];

    // The open ones, made again as one opens or closes, so Windows hands out the same list until then.
    private readonly List<IWindow> _open = [];

    private uint _main; // the main window's handle; closing it ends the game

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

    public IReadOnlyList<IWindow> Windows => _open;

    public IWindow? Get(uint handle)
    {
        return _byHandle.TryGetValue(handle, out Window? window) && window.IsOpen ? window : null;
    }

    /// <summary>
    /// On the render thread, which SDL's video functions want: whoever asks waits.
    /// </summary>
    public string Text
    {
        get => _threads.Invoke(ThreadId.Render, () => SDL.HasClipboardText() ? SDL.GetClipboardText() ?? "" : "");
        set => _threads.Invoke(ThreadId.Render, () =>
        {
            if (!SDL.SetClipboardText(value))
                Log.Warn($"The clipboard could not be set: {SDL.GetError()}");
        });
    }

    private Window Create(string title, int width, int height, WindowMode mode)
    {
        Window newWindow = _threads.Invoke(ThreadId.Render, () => new Window(title, width, height, mode, _assets, _threads, _projectIcon));

        _byHandle[newWindow.Handle] = newWindow;
        _ordered.Add(newWindow);
        ListOpen();

        return newWindow;
    }

    /// <summary>
    /// The window the game shows in, titled with the project's name (and DEBUG in a Debug build).
    /// </summary>
    private void OpenMainWindow(string name, WindowSettings settings)
    {
#if DEBUG
        string title = $"{name} (DEBUG)";
#else
        string title = name;
#endif
        Window main = Create(title, settings.Width, settings.Height, settings.Mode);
        _main = main.Handle;
        main.Show();
    }

    private void OnWindowEvent(ref SDL.Event e)
    {
        switch ((SDL.EventType)e.Type)
        {
            case SDL.EventType.WindowCloseRequested:
                OnCloseRequested(e.Window.WindowID);
                break;
            case SDL.EventType.Quit:
                foreach (Window window in _ordered)
                    window.Close();
                ListOpen();
                _events.Publish(new Quit());
                break;
            case SDL.EventType.WindowResized:
            case SDL.EventType.WindowPixelSizeChanged:
                if (_byHandle.TryGetValue(e.Window.WindowID, out Window? resized))
                    resized.ReadSizes();
                break;
            case SDL.EventType.WindowFocusGained:
            case SDL.EventType.WindowFocusLost:
                _events.Publish(new WindowFocus(e.Window.WindowID, (SDL.EventType)e.Type == SDL.EventType.WindowFocusGained));
                break;
        }
    }

    // A close request must not destroy the SDL_Window*: a GPU device may have claimed it, and that claim is
    // only released when the render gem unloads, which happens after the game has ended. So the window is marked
    // closed and hidden here, and destroyed by CloseAll as the gem unloads. The main window ends the game.
    private void OnCloseRequested(uint handle)
    {
        if (!_byHandle.TryGetValue(handle, out Window? window))
            return;

        window.Close();
        ListOpen();
        if (handle == _main)
            _events.Publish(new Quit());
    }

    private void ListOpen()
    {
        _open.Clear();
        foreach (Window window in _ordered)
        {
            if (window.IsOpen)
                _open.Add(window);
        }
    }

    private void CloseAll()
    {
        foreach (Window window in _ordered)
            window.Dispose();

        _ordered.Clear();
        _byHandle.Clear();
        _open.Clear();
    }

    private sealed class Window : IWindow, IDisposable
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
            if (_window == 0)
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
            if (_window == 0)
                return;

            IsOpen = false;
            OnRenderThread(() => SDL.DestroyWindow(_window));
            _window = 0;
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

            if (_window != 0)
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
                    Log.Warn($"{icon.Path} cannot be a window icon: it is {icon.Format}, and an icon must be uncompressed RGBA.");
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
                Log.Warn($"The window icon could not be loaded: {ex.Message}");
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
            if (load is null || _window == 0)
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
                if (surface == 0)
                {
                    Log.Warn($"Could not wrap {icon.Path} as a surface: {SDL.GetError()}");
                    return;
                }

                if (!SDL.SetWindowIcon(_window, surface))
                    Log.Warn($"Could not set {icon.Path} as the window icon: {SDL.GetError()}");
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
        nint hwnd = SDL.GetPointerProperty(SDL.GetWindowProperties(window), SDL.Props.WindowWin32HWNDPointer, 0);
        if (hwnd == 0)
            return;

        nint big = SendMessageW(hwnd, WM_GETICON, ICON_BIG, 0);
        if (big != 0)
            SetClassLongPtrW(hwnd, GCLP_HICON, big);

        nint small = SendMessageW(hwnd, WM_GETICON, ICON_SMALL, 0);
        if (small != 0)
            SetClassLongPtrW(hwnd, GCLP_HICONSM, small);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetClassLongPtrW(nint hwnd, int index, nint value);
}
