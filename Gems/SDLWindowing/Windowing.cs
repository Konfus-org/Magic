using Magic.Attributes;
using Magic.Contexts;
using Magic.Contexts.Assets;
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
/// table or need callbacks from the factory to stay in sync; neither buys anything.
/// </summary>
[Gem(name: "SDL Windowing", version: "1.0.0", description: "Provides SDL windowing and window event handling.", author: "Konfus", isStatic: true, DependsOn = ["SDL"])]
[GemExport(typeof(IWindowFactory), typeof(IWindowRegistry))]
internal sealed class WindowManager : IWindowFactory, IWindowRegistry, IDisposable
{
    private readonly AssetManager _assets;
    private readonly Handle<Texture> _projectIcon;

    // Windows by handle (the SDL window id): that is what window events carry. A window stays here after
    // it is closed, until it is disposed: closing hides it, disposing destroys it (see OnCloseRequested).
    private readonly Dictionary<uint, Window> _byHandle = [];
    // The same windows in creation order, so Main is "the first one still open" rather than dictionary luck.
    private readonly List<Window> _ordered = [];
    // Kept in a field: SDL holds the native function pointer, and a collected delegate would crash the next event.
    private readonly SDL.EventFilter _watch;

    public WindowManager(Project project, AssetManager assets)
    {
        _assets = assets;
        _projectIcon = project.Icon;

        // The SDL gem owns SDL_Init/SDL_Quit and the event pump; this one only adds the subsystem it uses and
        // watches the queue. SDL runs the watch for every event as it is queued, from the pump on the main thread.
        if (!SDL.InitSubSystem(SDL.InitFlags.Video))
            throw new InvalidOperationException($"SDL video initialization failed: {SDL.GetError()}");
        Debugging.LogInfo($"SDL video initialised on thread {Environment.CurrentManagedThreadId}.");

        _watch = OnEvent;
        SDL.AddEventWatch(_watch, IntPtr.Zero);
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

    public void Dispose()
    {
        SDL.RemoveEventWatch(_watch, IntPtr.Zero);
        CloseAll();
        SDL.QuitSubSystem(SDL.InitFlags.Video);
    }

    public IWindow Create(string title, int width, int height, WindowMode mode)
    {
        Window newWindow = new(title, width, height, mode, _assets, _projectIcon);
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

    /// <summary>Sees every event as it is queued; always lets it through for whoever else watches.</summary>
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
        }
        return true;
    }

    private sealed class Window : IWindow
    {
        private readonly AssetManager _assets;
        // The SDL_Window* stays private; the host only ever sees the id.
        private nint _window;

        public Window(string title, int width, int height, WindowMode mode, AssetManager assets, Handle<Texture> icon)
        {
            _assets = assets;
            _window = SDL.CreateWindow(title, width, height, SDL.WindowFlags.Hidden | SDL.WindowFlags.Resizable);
            if (_window == IntPtr.Zero)
                throw new InvalidOperationException($"SDL window creation failed: {SDL.GetError()}");
            Handle = SDL.GetWindowID(_window);
            IsOpen = true;

            Title = title;
            Size = new Size(width, height);
            Mode = mode;
            Icon = icon;
        }

        /// <summary>False once closed; the SDL window itself lives on until <see cref="Dispose"/>.</summary>
        public bool IsOpen { get; private set; }

        public uint Handle { get; }

        public string Title
        {
            get;
            set
            {
                field = value;
                SDL.SetWindowTitle(_window, value);
            }
        }

        /// <summary>
        /// The texture shown as the window's icon; <see cref="Handle{T}.None"/> leaves the OS default. The
        /// texture is loaded here, on the calling thread, and its top mip level handed to SDL, which keeps
        /// its own copy.
        /// </summary>
        public Handle<Texture> Icon
        {
            get;
            set
            {
                field = value;
                if (!value.IsValid)
                    return;
                Texture? icon = _assets.Load(value);
                if (icon is null)
                    return; // the manager logged why
                if (icon.Format is not (TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb) || icon.Levels.Length == 0)
                {
                    Debugging.LogWarning($"{icon.Path} cannot be a window icon: it is {icon.Format}, and an icon must be uncompressed RGBA.");
                    return;
                }

                // CreateSurfaceFrom wraps the pixels in place, so they stay pinned until SDL has taken its copy.
                TextureLevel level = icon.Levels[0];
                GCHandle pin = GCHandle.Alloc(icon.Pixels, GCHandleType.Pinned);
                try
                {
                    nint surface = SDL.CreateSurfaceFrom(level.Width, level.Height, SDL.PixelFormat.ABGR8888, pin.AddrOfPinnedObject() + level.Offset, level.Width * 4);
                    if (surface == IntPtr.Zero)
                    {
                        Debugging.LogWarning($"Could not wrap {icon.Path} as a surface: {SDL.GetError()}");
                        return;
                    }
                    if (!SDL.SetWindowIcon(_window, surface))
                        Debugging.LogWarning($"Could not set {icon.Path} as the window icon: {SDL.GetError()}");
                    SDL.DestroySurface(surface);
                }
                finally
                {
                    pin.Free();
                }
            }
        }

        public Size Size
        {
            get;
            set
            {
                field = value;
                SDL.SetWindowSize(_window, value.Width, value.Height);
            }
        }

        public WindowMode Mode
        {
            get;
            set
            {
                field = value;
                switch (value)
                {
                    case WindowMode.Windowed:
                        SDL.SetWindowFullscreen(_window, false);
                        SDL.SetWindowBordered(_window, true);
                        break;
                    case WindowMode.Fullscreen:
                        SDL.SetWindowBordered(_window, true);
                        SDL.SetWindowFullscreen(_window, true);
                        break;
                    case WindowMode.Borderless:
                        SDL.SetWindowFullscreen(_window, false);
                        SDL.SetWindowBordered(_window, false);
                        break;
                }
            }
        }

        public void Show()
        {
            SDL.ShowWindow(_window);
        }

        public void Hide()
        {
            SDL.HideWindow(_window);
        }

        public void Minimize()
        {
            SDL.MinimizeWindow(_window);
        }

        public void Maximize()
        {
            SDL.MaximizeWindow(_window);
        }

        /// <summary>Marks the window closed and hides it, keeping the SDL window for whoever still holds it (a GPU device).</summary>
        public void Close()
        {
            IsOpen = false;
            if (_window != IntPtr.Zero)
                SDL.HideWindow(_window);
        }

        public void Dispose()
        {
            if (_window == IntPtr.Zero)
                return;

            IsOpen = false;
            SDL.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }
}
