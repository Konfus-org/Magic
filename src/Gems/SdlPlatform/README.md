# SdlPlatform

The window, clipboard and input, through [SDL3](https://github.com/libsdl-org/SDL). Provides `IWindow`, `IClipboard`
and `IInput`, and pumps the OS's events on the platform thread (`IPlatform`).

To use it, list `SdlPlatform` in a project's `.magic` file.

- Keyboards, mice and gamepads can be plugged in and out while running.
- Keys are physical keys (SDL scancodes), the same on every layout. Typed text follows the layout.
- Mouse position and motion are in window pixels, the motion scaled and eased as the settings say. The wheel is in
  notches.
- Gamepads use SDL's standard layout. Sticks go from -1 to 1 with up positive. Triggers go from 0 to 1.
- The window opens as `Window.Mode` says: fullscreen without a border in a Release build, and maximized in any other
  build, on the display `Window.Display` names. `set Window.Mode fullscreen|maximized|windowed` and
  `set Window.Display 1` change them while it runs.
- It paces the frames: `Window.MaxFrameRate` caps them while the window has the focus, and
  `Window.BackgroundFrameRate` while it hasn't, 0 for no cap. With `Simulation.PauseWhenUnfocused`, losing the focus
  pauses the world and getting it back resumes it. The wait shows as the gem's Update in the stats.
- The gem's settings are `SdlPlatformSettings`, under `Window`, `Input` and `Simulation`; `get Window`, `get Input`
  and `get Simulation` list them.
- The window's title is the project's name, and its icon the default one until `Icon` is set. Closing the window ends the run.
- The window and clipboard's members are the game thread's, and throw on any other; what is set reaches the OS on the
  platform thread's next pump. The window's size is read from any.
- Headless, it opens no window and needs no display. Only gamepads give input, and the clipboard's text stays in the gem.
- It publishes the RAM only this process holds, as `process.memory.usage` on its `Magic.Gems.SdlPlatform` meter: the
  private working set on Windows, the anonymous resident pages (`RssAnon`) on Linux, and the physical footprint on
  macOS, the numbers the OS's own tools show. They come from the OS through bindings written in this gem.
