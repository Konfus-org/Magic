using System.Diagnostics.Metrics;
using Magic.Files;
using Magic.Platform;
using Magic.SdlBindings;
using Magic.Utils;
using static Magic.SdlBindings.SDL_EventType;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlPlatform;

/// <summary>
/// The platform thread's side of SDL: it pumps SDL's events and hands them to the game thread as they came, and gives
/// the window and clipboard what the game thread set on them. It also measures the RAM the process holds.
/// </summary>
public sealed class Platform : IPlatform, IDisposable
{
    /// <summary>
    /// The longest a pump waits for the OS's next event, so what was handed to the platform thread is never kept longer.
    /// </summary>
    private const int PumpWaitMs = 10;

    private readonly SdlWindow _sdl;

    private readonly Meter _meter;

    internal Platform(SdlWindow sdl, IFileSystem files)
    {
        _sdl = sdl;

        // The gem's meter goes with it, so nothing holds the gem's code once it unloads. The OS is asked only when the
        // meter is read, as the number is the process's rather than a frame's.
        _meter = new Meter("Magic.Gems.SdlPlatform");
        _meter.CreateObservableGauge<long>(
            "magic.gems.sdl_platform.process.memory.usage",
            () => ProcessMemory.GetPrivateResidentBytes(files) is { } bytes ? [new(bytes)] : [],
            "By",
            "RAM only this process holds, as the OS's tools show it."
        );
    }

    public void Dispose() => _meter.Dispose();

    /// <summary>
    /// Gives the window and clipboard what the game thread set, then hands the game thread SDL's events since the last
    /// pump, waiting up to <see cref="PumpWaitMs"/> for one when there are none. Platform thread.
    /// </summary>
    public unsafe void Pump()
    {
        if (_sdl.Pointer != null)
        {
            if (Interlocked.Exchange(ref _sdl.TitleToShow, null) is { } title)
            {
                fixed (byte* text = CString.ToBytes(title))
                {
                    SDL_SetWindowTitle(_sdl.Pointer, text);
                }
            }

            if (Interlocked.Exchange(ref _sdl.ClipboardToSet, null) is { } clipboard)
            {
                fixed (byte* text = CString.ToBytes(clipboard))
                {
                    SDL_SetClipboardText(text);
                }
            }

            Volatile.Write(ref _sdl.PixelDensity, SDL_GetWindowPixelDensity(_sdl.Pointer));
            _sdl.ReadSize();
        }

        SDL_Event sdlEvent;
        if (!SDL_WaitEventTimeout(&sdlEvent, PumpWaitMs))
            return;

        do
        {
            SDL_EventType type = (SDL_EventType)sdlEvent.type;
            string text = string.Empty;
            if (type == SDL_EVENT_KEYBOARD_ADDED)
                text = CString.Read(SDL_GetKeyboardNameForID(sdlEvent.kdevice.which));
            else if (type == SDL_EVENT_MOUSE_ADDED)
                text = CString.Read(SDL_GetMouseNameForID(sdlEvent.mdevice.which));
            else if (type == SDL_EVENT_TEXT_INPUT)
                text = CString.Read(sdlEvent.text.text);
            else if (type == SDL_EVENT_CLIPBOARD_UPDATE)
                Volatile.Write(ref _sdl.Clipboard, SdlWindow.ReadClipboard());

            _sdl.Events.Writer.TryWrite((sdlEvent, text));
        } while (SDL_PollEvent(&sdlEvent));
    }
}
