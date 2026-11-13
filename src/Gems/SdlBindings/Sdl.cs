using Magic.Utils;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlBindings;

/// <summary>
/// SDL's C API, as C#.
/// </summary>
public static class Sdl
{
    /// <summary>
    /// SDL's reason for the last call that failed.
    /// </summary>
    public static unsafe string Error => CString.Read(SDL_GetError());
}
