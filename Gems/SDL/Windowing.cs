using Magic.Attributes;
using Magic.Interfaces;
using Magic.Services;
using SDL3;
using System.Drawing;

namespace SDLGem;


[Gem("SDL Windowing", "1.0.0", "Provides SDL windowing and window event handling.", Author = "Konfus")]
[GemExport]
internal class WindowManager : IWindowFactory, IDisposable
{
    // Open windows by SDL window id, so the event loop can tell a window it was asked to close.
    private readonly Dictionary<nint, Window> _open = [];
    private readonly IDisposable _eventSystem;

    public WindowManager(SystemRegistry systems)
    {
        if (!SDL.Init(SDL.InitFlags.Video))
            throw new InvalidOperationException($"SDL initialization failed: {SDL.GetError()}");

        // Runs every frame on the main thread: SDL wants the OS message queue pumped from the thread
        // that created the windows, and Windows flags a window as unresponsive if it isn't.
        _eventSystem = systems.Register(PumpEvents, UpdateType.Update);
    }

    public void Dispose()
    {
        _eventSystem.Dispose();
        SDL.Quit();
    }

    public IWindow Create(string title, int width, int height, WindowMode mode)
    {
        Window newWindow = new(title, width, height, mode);
        _open[newWindow.Handle] = newWindow;
        return newWindow;
    }

    private void OnCloseRequested(nint windowId)
    {
        if (_open.Remove(windowId, out Window? window))
            window.Dispose();
    }

    private void CloseAll()
    {
        foreach (Window window in _open.Values.ToArray())
            window.Dispose();
        _open.Clear();
    }

    private void PumpEvents(double deltaTime)
    {
        while (SDL.PollEvent(out SDL.Event e))
        {
            switch ((SDL.EventType)e.Type)
            {
                case SDL.EventType.WindowCloseRequested:
                    OnCloseRequested((nint)e.Window.WindowID);
                    break;
                case SDL.EventType.Quit:
                    CloseAll();
                    break;
            }
        }
    }

    private class Window : IWindow
    {
        public Window(string title, int width, int height, WindowMode mode)
        {
            Handle = SDL.CreateWindow(title, width, height, SDL.WindowFlags.Hidden | SDL.WindowFlags.Resizable);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException($"SDL window creation failed: {SDL.GetError()}");

            Title = title;
            Size = new Size(width, height);
            Mode = mode;
        }

        public bool IsOpen => Handle != IntPtr.Zero;

        public nint Handle { get; private set; }

        public string Title
        {
            get => field;
            set
            {
                field = value;
                SDL.SetWindowTitle(Handle, value);
            }
        }

        public Guid Icon
        {
            get => field;
            set
            {
                field = value;
                // TODO: Implement icon setting logic using SDL_SetWindowIcon. This may involve loading an image from a file or resource and converting it to an SDL_Surface.
                SDL.SetWindowIcon(Handle, IntPtr.Zero);
            }
        }

        public Size Size
        {
            get => field;
            set
            {
                field = value;
                SDL.SetWindowSize(Handle, value.Width, value.Height);
            }
        }

        public WindowMode Mode
        {
            get => field;
            set
            {
                field = value;
                switch (value)
                {
                    case WindowMode.Windowed:
                        SDL.SetWindowFullscreen(Handle, false);
                        break;
                    case WindowMode.Fullscreen:
                        SDL.SetWindowFullscreen(Handle, true);
                        break;
                    case WindowMode.Borderless:
                        SDL.SetWindowBordered(Handle, false);
                        break;
                }
            }
        }

        public void Show()
        {
            SDL.ShowWindow(Handle);
        }

        public void Hide()
        {
            SDL.HideWindow(Handle);
        }

        public void Minimize()
        {
            SDL.MinimizeWindow(Handle);
        }

        public void Maximize()
        {
            SDL.MaximizeWindow(Handle);
        }

        public void Dispose()
        {
            if (!IsOpen) return;

            SDL.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
