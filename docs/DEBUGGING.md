# Debugging

This doc tells you how to use the debug UI and the debug windows, and how a gem publishes its measurements.

## ImGuiDebugUI

Draws the debug UI with [Dear ImGui](https://github.com/ocornut/imgui). Provides `IDebugUI`.

The debug UI shows what gems and scripts report: lines at the left of the picture, and short text at positions in the
world. A line logged with `onScreen` also shows there for some time. Other gems draw their debug windows with its
widgets.

In the `.magic` file of a project, list it with the GPU provider, the shader compiler and the ECS. Magic makes a gem
that takes `IDebugUI` after this gem.

- The UI draws over the window image, so it shows over the world. It is not in screenshots. A headless run has no
  window, so it draws no UI.
- Lines and text that gems ask for always show.
- Lines logged with `onScreen` show while a debug window is open. The settings can make them show at all times. In a
  Debug build, they show at all times by default.
- The settings of the gem are in `ImGuiDebugUISettings`, in the `Debug` category. `get Debug` lists them.
- The UI scales with the display until you set `Debug.UiScale`.
- Without a gem that provides `IInput`, you cannot click or type in the UI. Without a gem that provides `IClipboard`,
  you cannot copy or paste.
- The UI draws nothing until its shaders compile.
- The text uses the font of ImGui.

Dear ImGui is a native library. The gem calls it through the C API that
[dear_bindings](https://github.com/dearimgui/dear_bindings) generates ([Native libraries](GEMS.md#native-libraries)).

## DebugTools

Draws debug lines and windows for the measurements of the meters and for the settings of the gems. It provides
nothing. It needs a gem that provides `IDebugUI`.

| Tool | Key | Setting | Shows |
| --- | --- | --- | --- |
| Counter | `Debug.CounterKey` (F1) | `Debug.IsCounterShown` | The frame rate, the frame time, and the memory of the GPU and the process, in the top-left corner |
| Stats | `Debug.StatsKey` (F3) | `Debug.IsStatsOpen` | Each instrument of the `Magic.*` meters and the `System.Runtime` meter of .NET |
| Settings | `Debug.SettingsKey` (F4) | `Debug.IsSettingsOpen` | The settings of each gem |

- In a Debug build, the counter shows from the start. In other builds, the counter line shows only while a debug
  window is open.
- The gem collects the numbers each `Debug.StatsCollectSec`, and only while the counter or the stats window shows.
- Without a gem that provides input, no key works.

### Stats

The stats window has sections that fold:

- **CPU** shows the frame of the game thread and of the render thread. Under each, it shows the time of each step of
  Core and of the hooks of each gem, the slowest first.
- **GPU** shows what the GPU provider tells about the API and the GPU, its frame and its passes.
- **Memory** shows the memory of the process and the GPU, then the instruments of Core, the process and each gem.
- **Other** holds the other instruments, by meter.

A histogram shows its mean, its count each second and its maximum. A counter shows its increase and its increase each
second. A gauge shows its last value.

### Settings

The settings window has a header for each category. Each setting has a control for its type, and its summary as a
tooltip.

- A change applies at once.
- A setting marked `(restarts)` applies when you click its button. This restarts the run.
- **Save** keeps the settings for this user and project.

## Measuring

Core and each gem publish their measurements on their own `System.Diagnostics.Metrics` meter.

- Core uses the `Magic.Core` meter. A gem uses `Magic.Gems.<Gem>`, with instruments named
  `magic.gems.<gem_snake_case>.*`.
- Time is in seconds. Memory is in bytes.
- Magic makes the meter of a gem with the gem, and disposes it with the gem.

To publish a measurement from a gem:

1. For a value of each frame, record a gauge each frame. Do not make an observable gauge.
2. To time work, use a histogram of seconds with its `Start` and `RecordSince`. They do nothing while nothing listens.
3. For a number that the OS keeps for the whole process, use an observable gauge. Magic reads it when it needs it.

Where a measurement shows in the stats window:

- Core times the hooks of each gem and its own steps. A gem needs nothing more to show under CPU.
- An instrument named `gpu.*` shows under GPU. The values of its tags are the rows of a fold of its own. The tags of
  the meter are above it.
- An instrument in bytes shows under its gem in Memory. An instrument named `memory.usage` is the total of the gem.
  `gpu.memory.usage` also shows in the counter as the memory of the GPU.
- `process.memory.usage` is the RAM in the counter and in the Memory header. The platform gem publishes it. Without a
  platform gem, they show the working set of .NET, which also counts the pages that the process shares.

The console command `stats [category] <seconds>` of Core logs the instruments as text, each so many seconds. It works
with or without the debug tools gem. For example, `stats gpu 30` logs the instruments with `gpu` in their name.
