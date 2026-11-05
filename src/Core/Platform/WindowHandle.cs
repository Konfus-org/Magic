namespace Magic.Platform;

/// <summary>
/// Which window system a window belongs to, which says what its <see cref="WindowHandle"/> holds.
/// </summary>
public enum WindowSystem : byte
{
    /// <summary>
    /// There is no window.
    /// </summary>
    None,
    Windows,
    X11,
    Wayland,
    Cocoa,
}

/// <summary>
/// What the OS knows a window by, as plain values, for whatever draws into it to make what its graphics API needs.
/// </summary>
/// <param name="System">The window system, which says what the others are.</param>
/// <param name="Display">The Win32 module, the X11 display or the Wayland display; 0 on Cocoa.</param>
/// <param name="Window">The <c>HWND</c>, the X11 window, the <c>wl_surface</c> or the <c>CAMetalLayer</c>.</param>
public readonly record struct WindowHandle(WindowSystem System, nint Display, nint Window);
