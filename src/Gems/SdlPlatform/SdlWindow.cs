using System.Threading.Channels;
using Magic.Debugging;
using Magic.SdlBindings;
using Magic.Threading;
using Magic.Utils;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlPlatform;

/// <summary>
/// SDL for the gem's run, with its window and the data that the platform and game threads hand each other.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Platform"/> pumps SDL's events and gives the window and clipboard what was set on them.
/// <see cref="Window"/>, <see cref="Clipboard"/> and <see cref="Input"/> read and set the rest. Headless, it starts
/// only SDL's gamepads and opens no window.
/// </para>
/// <para>
/// SDL's windows and events belong to the process's first thread, the platform thread, which makes the gem.
/// </para>
/// </remarks>
internal sealed unsafe class SdlWindow : IDisposable
{
    /// <summary>
    /// The window; null when headless.
    /// </summary>
    public readonly SDL_Window* Pointer;

    /// <summary>
    /// What holds the window's Metal layer on macOS, so its handle has one to name; null elsewhere.
    /// </summary>
    public readonly void* MetalView;

    /// <summary>
    /// SDL's events, pumped on the platform thread and applied on the game thread, each with the text SDL holds only
    /// until the platform thread's next pump: the name of the keyboard or mouse it adds, or what was typed.
    /// </summary>
    public readonly Channel<(SDL_Event Event, string Text)> Events = Channel.CreateUnbounded<(SDL_Event, string)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// What the game thread set on the window and clipboard, each written whole and taken by the platform thread's next
    /// pump; null once taken.
    /// </summary>
    public string? TitleToShow;

    public string? ClipboardToSet;

    /// <summary>
    /// What the window's clipboard holds, as its last event said; headless, only what was set on it.
    /// </summary>
    public string Clipboard = string.Empty;

    /// <summary>
    /// Window pixels to SDL's points, written whole by each pump for the game thread's mouse motion.
    /// </summary>
    public float PixelDensity = 1;

    /// <summary>
    /// The window's size in pixels and its display's scale, read by the platform thread as the window opens and on each
    /// pump, and written whole for any thread; 0 and 1 when headless.
    /// </summary>
    public int Width;

    public int Height;

    public float DisplayScale = 1;

    /// <summary>
    /// Whether the window has the keyboard's focus, as its last focus event said; always true when headless.
    /// </summary>
    /// <remarks>
    /// Use it only on the game thread.
    /// </remarks>
    public bool IsFocused = true;

    public SdlWindow(SdlPlatformSettings settings, string title, bool isHeadless)
    {
        uint systems = isHeadless ? SDL_INIT_GAMEPAD : SDL_INIT_VIDEO | SDL_INIT_GAMEPAD;
        if (!SDL_Init(systems))
            throw new InvalidOperationException($"SDL could not start: {Sdl.Error}");

        if (isHeadless)
            return;

        ulong mode = settings.Mode switch
        {
            WindowMode.Fullscreen => SDL_WINDOW_FULLSCREEN,
            WindowMode.Maximized => SDL_WINDOW_MAXIMIZED,
            WindowMode.Windowed => 0,
            _ => throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Mode,
                "There is no such window mode."
            ),
        };

        // macOS draws through a Metal layer, which the window must be opened for; elsewhere the OS's window is enough. The
        // window opens centred on its display, as one already fullscreen or maximized can't be moved there.
        ulong flags =
            mode
            | (settings.IsResizable ? SDL_WINDOW_RESIZABLE : 0)
            | SDL_WINDOW_HIGH_PIXEL_DENSITY
            | (OperatingSystem.IsMacOS() ? SDL_WINDOW_METAL : 0);
        long centered = SDL_WINDOWPOS_CENTERED_MASK | FindDisplay(settings.Display);
        uint properties = SDL_CreateProperties();
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_FLAGS_NUMBER, (long)flags);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_X_NUMBER, centered);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_Y_NUMBER, centered);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, settings.WindowedSize.Width);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, settings.WindowedSize.Height);
        fixed (
            byte* name = SDL_PROP_WINDOW_CREATE_TITLE_STRING,
                text = CString.ToBytes(title)
        )
        {
            SDL_SetStringProperty(properties, name, text);
        }

        Pointer = SDL_CreateWindowWithProperties(properties);
        SDL_DestroyProperties(properties);

        if (Pointer == null)
        {
            string error = Sdl.Error;
            SDL_Quit();

            throw new InvalidOperationException($"SDL could not open a window: {error}");
        }

        if (OperatingSystem.IsMacOS())
            MetalView = SDL_Metal_CreateView(Pointer);

        ReadSize();
        IsFocused = (SDL_GetWindowFlags(Pointer) & SDL_WINDOW_INPUT_FOCUS) != 0;

        // Typed characters come as text events only while text input is on: always, as a desktop has no on-screen
        // keyboard for it to bring up.
        if (!SDL_StartTextInput(Pointer))
            Log.Warn($"Typed text is not read: {Sdl.Error}");

        Clipboard = ReadClipboard();
    }

    /// <summary>
    /// The clipboard's text, which SDL hands out as a copy that is freed once read.
    /// </summary>
    public static string ReadClipboard()
    {
        byte* text = SDL_GetClipboardText();
        string value = CString.Read(text);
        SDL_free(text);

        return value;
    }

    /// <summary>
    /// Finds SDL's id of the display at <paramref name="index"/> in its list; the main display, with a warning, when
    /// there is no such display.
    /// </summary>
    public static uint FindDisplay(int index)
    {
        int count;
        uint* displays = SDL_GetDisplays(&count);
        uint display = index >= 0 && index < count ? displays[index] : SDL_GetPrimaryDisplay();
        SDL_free(displays);
        if (index < 0 || index >= count)
            Log.Warn($"There is no display {index}: there are {count}, from 0. The window shows on the main one.");

        return display;
    }

    /// <summary>
    /// Throws if the caller is not on the game thread, because SDL's window and clipboard belong to it.
    /// </summary>
    /// <remarks>
    /// On some systems, a call from a worker works, and on others it crashes.
    /// </remarks>
    public static void ThrowIfNotOnGame(Threads threads)
    {
        if (!threads.IsCurrent(EngineThread.Game))
            throw new InvalidOperationException("The window and clipboard are the game thread's.");
    }

    public void Dispose()
    {
        if (MetalView != null)
            SDL_Metal_DestroyView(MetalView);

        if (Pointer != null)
            SDL_DestroyWindow(Pointer);

        SDL_Quit();
    }

    /// <summary>
    /// Reads the window's size in pixels and its display's scale, and writes them whole for any thread to read.
    /// </summary>
    /// <remarks>
    /// Call it only on the platform thread.
    /// </remarks>
    public void ReadSize()
    {
        int width = 0;
        int height = 0;
        SDL_GetWindowSizeInPixels(Pointer, &width, &height);
        Volatile.Write(ref Width, width);
        Volatile.Write(ref Height, height);
        Volatile.Write(ref DisplayScale, SDL_GetWindowDisplayScale(Pointer));
    }

    /// <summary>
    /// Sets the named number in <paramref name="properties"/>.
    /// </summary>
    private static void SetNumber(uint properties, ReadOnlySpan<byte> name, long value)
    {
        fixed (byte* key = name)
        {
            SDL_SetNumberProperty(properties, key, value);
        }
    }
}
