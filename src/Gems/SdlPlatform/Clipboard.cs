using Magic.Platform;
using Magic.Threading;

namespace Magic.SdlPlatform;

/// <summary>
/// The window's clipboard, through SDL3. Headless, it keeps its text to itself. What the game thread sets is handed to
/// the platform thread, which gives it to the OS.
/// </summary>
public sealed class Clipboard : IClipboard
{
    private readonly SdlWindow _sdl;

    private readonly Threads _threads;

    internal Clipboard(SdlWindow sdl, Threads threads)
    {
        _sdl = sdl;
        _threads = threads;
    }

    /// <summary>
    /// What the window's clipboard holds, as its last event said; headless, only what was set here.
    /// </summary>
    public unsafe string Text
    {
        get => Volatile.Read(ref _sdl.Clipboard);
        set
        {
            SdlWindow.ThrowIfNotOnGame(_threads);
            Volatile.Write(ref _sdl.Clipboard, value);
            if (_sdl.Pointer != null)
                Volatile.Write(ref _sdl.ClipboardToSet, value);
        }
    }
}
