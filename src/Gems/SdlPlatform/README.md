# SdlPlatform

The window, clipboard and input, through [SDL3](https://github.com/libsdl-org/SDL). Provides `IWindow`, `IClipboard`
and `IInput`.

To use it, list `SdlPlatform` in a project's `.magic` file.

- Keyboards, mice and gamepads can be plugged in and out while running.
- Keys are physical keys (SDL scancodes), the same on every layout. Typed text follows the layout.
- Mouse position and motion are in window pixels. The wheel is in notches.
- Gamepads use SDL's standard layout. Sticks go from -1 to 1 with up positive. Triggers go from 0 to 1.
- The window's title is the project's name. Closing the window ends the run.
- The window and clipboard's members are the main thread's, and throw on any other.
- Headless, it opens no window and needs no display. Only gamepads give input, and the clipboard's text stays in the gem.
