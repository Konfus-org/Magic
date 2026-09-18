using Magic.Attributes;
using Magic.Interfaces;
using SDL3;
using System.Drawing;

namespace SDLGem;

[GemExport]
internal class WindowManager : IWindowFactory
{
    // Open windows by SDL window id, so the event loop can tell a window it was asked to close.
    private static readonly Dictionary<uint, Window> _open = [];

    public IWindow Create(string title, int width, int height, WindowMode mode)
    {
        return new Window(title, width, height, mode);
    }

    internal static void OnCloseRequested(uint windowId)
    {
        if (_open.Remove(windowId, out Window? window))
            window.Dispose();
    }

    internal static void CloseAll()
    {
        foreach (Window window in _open.Values.ToArray())
            window.Dispose();
        _open.Clear();
    }

    private class Window : IWindow
    {
        private IntPtr _window;

        public Window(string title, int width, int height, WindowMode mode)
        {
            _window = SDL.CreateWindow(title, width, height, SDL.WindowFlags.Hidden);
            if (_window == IntPtr.Zero)
                throw new InvalidOperationException($"SDL window creation failed: {SDL.GetError()}");

            Title = title;
            Size = new Size(width, height);
            Mode = mode;
        }

        public string Title
        {
            get => field;
            set
            {
                field = value;
                SDL.SetWindowTitle(_window, value);
            }
        }

        public Guid Icon
        {
            get => field;
            set
            {
                field = value;
                // TODO: Implement icon setting logic using SDL_SetWindowIcon. This may involve loading an image from a file or resource and converting it to an SDL_Surface.
                SDL.SetWindowIcon(_window, IntPtr.Zero);
            }
        }

        public Size Size
        {
            get => field;
            set
            {
                field = value;
                SDL.SetWindowSize(_window, value.Width, value.Height);
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
                        SDL.SetWindowFullscreen(_window, false);
                        break;
                    case WindowMode.Fullscreen:
                        SDL.SetWindowFullscreen(_window, true);
                        break;
                    case WindowMode.Borderless:
                        SDL.SetWindowBordered(_window, false);
                        break;
                }
            }
        }

        public bool IsOpen => _window != IntPtr.Zero;

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

        public void Dispose()
        {
            if (!IsOpen) return;

            SDL.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }
}
