# SdlPlatform

The window, clipboard and input, through [SDL3](https://github.com/libsdl-org/SDL). Provides `IWindow`, `IClipboard`
and `IInput`, and pumps the OS's events on the platform thread (`IPlatform`).

To use it, list `SdlPlatform` in a project's `.magic` file.

- Keyboards, mice and gamepads can be plugged in and out while running.
- Keys are physical keys (SDL scancodes), the same on every layout. Typed text follows the layout.
- Mouse position and motion are in window pixels. The wheel is in notches.
- Gamepads use SDL's standard layout. Sticks go from -1 to 1 with up positive. Triggers go from 0 to 1.
- The window opens as `Window.Mode` says: fullscreen without a border in a Release build, and maximized in any other
  build. `set Window.Mode fullscreen|maximized|windowed` changes it while it runs.
- The gem's settings are `SdlPlatformSettings`, under `Window` and `Input`; `get Window` and `get Input` list them.
- The window's title is the project's name, and its icon the default one until `Icon` is set. Closing the window ends the run.
- The window and clipboard's members are the game thread's, and throw on any other; what is set reaches the OS on the
  platform thread's next pump. The window's size is read from any.
- Headless, it opens no window and needs no display. Only gamepads give input, and the clipboard's text stays in the gem.
- It publishes the RAM only this process holds, as `process.memory.usage` on its `Magic.Gems.SdlPlatform` meter: the
  private working set on Windows, the anonymous resident pages (`RssAnon`) on Linux, and the physical footprint on
  macOS, the numbers the OS's own tools show. They come from the OS through bindings written in this gem.
