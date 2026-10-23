# DebugTools

Debug windows, drawn with the debug UI's widgets ([The debug UI](../../../Core/README.md#the-debug-ui)). It provides
nothing and needs a gem that provides `IDebugUI`.

- **Console**, on the key left of 1 (`` ` `` on a US layout): the log, with a filter, and a line to type commands in.
  Type `help` to list them.
- **Telemetry**, on F3: what the engine measured over the last second, in tabs.
- **Settings**, on F4: every gem's settings, changed while running, plus the preset.

Right-click in a window and choose Copy to put what it shows on the clipboard. The frame rate shows in the top left
corner in a Debug build. It adds console commands to read and change settings and apply a preset.

Its settings, such as the frame-rate line and the telemetry tabs, are on `DebugToolsSettings` and show in the settings
window.

- Changes made in the settings window aren't saved. Copy gives them as `Gem.setting=value` lines, the form `--set`
  takes.
- Changing a setting marked `[RequireRestart]` restarts the engine. The world is saved and put back.
- Telemetry measures only while its window is open. Some measurements slow the frame while it is open.
- Without an input gem, no key opens a window.
