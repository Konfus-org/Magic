# SdlPlatform

The window and input, through SDL3, called through its generated API ([Gems](../README.md#native-libraries)), which the
SDL bindings project provides. Provides `IWindow` and `IInput`. SDL comes from the bindings project, and as this gem is
made before every other gem built on it, the copy it loads is the one they all share
([Sharing a library](../README.md#sharing-a-library)). As it starts, the gem checks that the SDL it got is at least
the version the binding was generated from, the oldest that serves it; SDL keeps its ABI, so a newer one does.

## Settings

Each setting is documented on `SdlPlatformSettings` and shown in the settings window. All are followed every frame: a
changed size resizes the window, while dragging the window changes it and leaves the size settings as they were; the
input settings take from the next frame.

## Behaviour

- SDL starts in the gem's constructor and stops in `Dispose`, on the render thread, which owns its windows and events.
- `Render`, on the render thread, pumps SDL's events, and writes what they change (devices, controls, typed text, the
  window's size, the clipboard) as plain data to a channel; `Update` applies it on the main thread, before the gems
  that take `IInput` or `IWindow`, so input, the window's size and the clipboard are fixed for the rest of the frame.
  Setting the window's title or clipboard through `IWindow`, or a size in the settings, is kept for the render thread,
  which gives it to the window as it next pumps.
- The window's title is the project's name, and the id `IWindow` gives it is SDL's id for it, which a GPU gem finds it
  by.
- The window's icon is the one `IWindow` names, the default icon until it is set. Setting it loads the icon and its
  texture through Core's `Assets` on workers, and the render thread gives the texture's first mip level to SDL; one
  that does not load is an error, and the window keeps the icon it has. The icon is read when it is set, so an edit to
  its files shows once it is set again.
- Closing the window, or the OS asking the program to quit, stops the run. SDL's own signal handling is off, so Ctrl+C
  is never turned into a quit event: in a `--headless` run Core catches it and stops the run cleanly, and in a run with
  a window it ends the process at once, as it does any program ([Core's README](../../../Core/README.md#the-run)).
- Headless, it opens no window and needs no display: `IWindow` is 0×0 with no clipboard, and only gamepads give
  input, since keyboards and mice need a window to type and point into.
- If SDL cannot start or open the window, the gem throws with SDL's error and is not made.

## Input

- Keyboards, mice and gamepads are devices as SDL reports them, plugged in and out while running. One physical device
  can show up as several (a keyboard with media keys, one receiver for a keyboard and a mouse).
- Keys are physical (SDL scancodes), so a key is the same key on every layout. What they type is the text `IInput`
  gives, as the layout and input method make it: SDL's text input is on for as long as the window is open.
- The mouse's position and motion are in window pixels, converted from SDL's points, the motion scaled by the
  sensitivity setting and flipped when the settings say; the wheel is in notches, away from the player positive
  whatever the OS's scrolling direction.
- A gamepad is a device once SDL has opened it; input from one it could not open is ignored.
- Gamepads use SDL's standard layout: buttons by position, sticks -1 to 1 with up positive (SDL's y grows downward)
  unless the settings invert them, triggers 0 to 1, each 0 inside the settings' dead zone and growing from there to 1
  at full deflection. Sticks and triggers are read every frame rather than added up from events.
