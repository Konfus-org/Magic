# Debugging

The debug UI, the debug windows, and how Core and the gems publish what they measure.

## ImGuiDebugUI

The debug UI, drawn with [Dear ImGui](https://github.com/ocornut/imgui): provides `IDebugUI`.

It shows what gems and scripts report: lines down the left of the picture and short text at places in the world. A
line logged with `onScreen` shows there for a while too. Other gems draw their debug windows with its widgets.

List it in a project's `.magic` file with the GPU provider, the shader compiler and the ECS. A gem that takes
`IDebugUI` is made after it.

- The UI draws itself over the window's image, in the overlay layer, so it shows over whatever drew the world. It is
  not in screenshots, and a headless run, which has no window, draws none.
- Lines and text that gems ask for always show. Lines logged with `onScreen` show while a debug window is open, or
  always when its settings say so, as they do by default in a Debug build.
- The gem's settings are `ImGuiDebugUISettings`, under `Debug`; `get Debug` lists them. Its size follows the
  display's scale until `Debug.UiScale` is set.
- Without a gem that provides `IInput`, nothing can be clicked or typed. Without one that provides `IClipboard`, nothing
  can be copied or pasted.
- Nothing is drawn until the UI's shaders compile.
- Text is written in ImGui's own font.

Dear ImGui is a native library, used through the C API that
[dear_bindings](https://github.com/dearimgui/dear_bindings) generates ([Native libraries](GEMS.md#native-libraries)).

## DebugTools

Debug lines and windows over what the meters measure and the gems' settings, drawn with the debug UI. It provides
nothing and needs a gem that provides `IDebugUI`.

- **Counter**, on `Debug.CounterKey` (F1) or `Debug.IsCounterShown`: the drawn frame rate and frame time, then the GPU's
  memory and the process's, as a line in the top left corner. It shows from the start in a Debug build. In other builds
  the debug UI shows lines only while a debug window is open.
- **Stats**, on `Debug.StatsKey` (F3) or `Debug.IsStatsOpen`: every instrument of the `Magic.*` meters and .NET's
  `System.Runtime`, in sections that fold. **CPU** shows the game thread's frame and the render thread's, then what
  Core's own steps and each gem's hooks take of it, each a fold of its own, the slowest first. **GPU** shows what the
  GPU provider says of the API and the GPU, its frame and its passes. **Memory** shows the process's and the GPU's, then
  Core's and the process's instruments, and each gem's. **Other** holds the rest, by meter. A histogram shows its mean,
  how many a second and its most, a counter how much it rose and how much a second, a gauge its latest value.
- **Settings**, on `Debug.SettingsKey` (F4) or `Debug.IsSettingsOpen`: every gem's settings, under a header per
  category, each with a control for its type and its summary as a tooltip. A change applies at once; one marked
  `(restarts)` applies with its button, which restarts the run. **Save** keeps them for this user and project.
- Numbers are collected every `Debug.StatsCollectSec`, and only while the counter or the stats window shows.
- Without a gem that provides input, no key works.

## Measuring

Core and the gems publish what they measure on their own `System.Diagnostics.Metrics` meter: Core on `Magic.Core`, a
gem on `Magic.Gems.<Gem>`, its instruments named `magic.gems.<gem_snake_case>.*`, time in seconds and memory in bytes.
A gem's meter is made with it and disposed with it. Record a gauge each frame rather than make an observable one,
and time work into a histogram of seconds with its `Start` and `RecordSince`, which do nothing while nothing listens.
A number the OS keeps for the whole process, rather than one of a frame, is an observable gauge, asked when read.
Core's console command `stats [category] <seconds>` also logs them, as text, every so many seconds, with or without
DebugTools: `stats gpu 30` logs those with gpu in their name.

- Core times each gem's hooks and its own steps; a gem needs nothing more to show under CPU.
- An instrument named `gpu.*` shows under GPU, with its tags' values as rows of a fold of its own, and the meter's tags
  above it.
- An instrument in bytes shows under its gem in Memory; one named `memory.usage` is the gem's total, and
  `gpu.memory.usage` also shows in the counter as the GPU's.
- `process.memory.usage`, OpenTelemetry's name for a process's memory, is the RAM in the counter and the Memory header:
  what only this process holds, from the platform gem. Without one, they show .NET's working set, which counts the
  pages the process shares with others too.
