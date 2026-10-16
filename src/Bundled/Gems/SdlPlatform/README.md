# SdlPlatform

The window and input, through SDL3 ([ppy.SDL3-CS](https://github.com/ppy/SDL3-CS)): provides `IWindow` and `IInput`.

## Settings

In the project's `Presets/Default.preset`, by another preset, or `--set SdlPlatform.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `width` | `1280` | The window's width, in screen points (pixels at 100% display scaling). |
| `height` | `720` | The window's height, in screen points. |
| `mouseSensitivity` | `1` | What the mouse's motion is multiplied by: above 1 turns faster, 0.1 to 10. |
| `invertMouseY` | `false` | Mouse motion up reads as down, and down as up. |
| `stickDeadZone` | `0.15` | How far a stick or trigger is pushed before it reads as anything but 0, 0 to 0.5 of its reach; past it, what is read grows from 0 to 1 at full deflection. |
| `invertStickY` | `false` | Pushing a stick up reads as down, and down as up. |

All are followed every frame. Changed while running, the window is resized, and dragging the window changes it and
leaves the size settings as they were; the input settings take from the next frame.

## Behaviour

- SDL starts in the gem's constructor and stops in `Dispose`, on the render thread, which owns its windows and events.
  Starting takes about 1.5 s, most of it finding gamepads.
- `Render`, on the render thread, pumps SDL's events, and writes what they change (devices, controls, typed text, the
  window's size, the clipboard) as plain data to a channel; `Update` applies it on the main thread, before the gems
  that take `IInput` or `IWindow`, so input, the window's size and the clipboard are fixed for the rest of the frame.
  Setting `IWindow.Title` or `IWindow.Clipboard` hands the change to the render thread.
- The window's title is the project's name, and its `IWindow.Id` is SDL's id for it, which a GPU gem finds it by.
- The window's icon is the one `IWindow.Icon` names, the default icon until it is set. Setting it loads the icon and
  its texture through Core's `Assets` on workers, then hands the texture's first mip level to SDL on the render thread;
  one that does not load is an error, and the window keeps the icon it has. The icon is read when it is set, so an edit
  to its files shows once it is set again.
- Closing the window, or the OS asking the program to quit, stops the run. SDL's own Ctrl+C handling is off: Ctrl+C is
  Core's (`--headless`).
- Headless, it opens no window and needs no display: `IWindow` is 0×0 with no clipboard, and only gamepads give
  input, since keyboards and mice need a window to type and point into.
- If SDL cannot start or open the window, the gem throws with SDL's error and is not made.

## Input

- Keyboards, mice and gamepads are devices as SDL reports them, plugged in and out while running. One physical device
  can show up as several (a keyboard with media keys, one receiver for a keyboard and a mouse).
- Keys are physical (SDL scancodes), so `KeyW` is the same key on every layout. What they type is `IInput.Text`, as
  the layout and input method make it: SDL's text input is on for as long as the window is open.
- The mouse's position and motion are in window pixels, converted from SDL's points, the motion multiplied by
  `mouseSensitivity` and flipped by `invertMouseY`; the wheel is in notches, away from the player positive whatever the
  OS's scrolling direction.
- A gamepad is a device once SDL has opened it; input from one it could not open is ignored.
- Gamepads use SDL's standard layout: buttons by position, sticks -1 to 1 with up positive (SDL's y grows downward)
  unless `invertStickY`, triggers 0 to 1, each 0 inside `stickDeadZone` and growing from there to 1 at full deflection.
  Sticks and triggers are read every frame rather than added up from events.

## Natives

ppy.SDL3-CS carries SDL for every platform it supports, and the gem's load context picks the one for the machine. The
SDL it loads is every gem's: a gem that binds SDL after it gets the library already loaded (`GemLoadContext`), which
is how a GPU gem draws into this gem's window.
