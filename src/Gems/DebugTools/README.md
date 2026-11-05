# DebugTools

Debug windows, drawn with the debug UI's widgets. It provides nothing and needs a gem that provides `IDebugUI`.

To use it, list `DebugTools` in a project's `.magic` file.

- **Stats**, on F3 or `set DebugTools.stats=true`: the frame rate, the main thread's time by hook, the GPU's time by
  pass, and the memory in use. Its numbers are averages over the last half second.
- CPU `Render` includes the waits for the GPU and the display.
- Without a gem that provides input, no key opens a window. Without a GPU provider, the window shows no GPU.
