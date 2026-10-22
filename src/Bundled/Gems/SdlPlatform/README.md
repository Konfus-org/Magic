# SdlPlatform

The window and input, through [SDL3](https://github.com/libsdl-org/SDL). Provides `IWindow` and `IInput`.

List it before the other gems built on SDL in the project's `.magic` file. Its settings, such as the window size and
mouse sensitivity, are on `SdlPlatformSettings` and show in the settings window (F4). They apply while running. SDL
comes from the SDL bindings project.

- Keyboards, mice and gamepads can be plugged in and out while running.
- Keys are physical keys (SDL scancodes), the same on every layout. Typed text follows the layout.
- Mouse position and motion are in window pixels. The wheel is in notches.
- Gamepads use SDL's standard layout. Sticks go from -1 to 1 with up positive. Triggers go from 0 to 1.
- The window's title is the project's name. Closing the window stops the run.
- Headless, it opens no window and needs no display. Only gamepads give input.
