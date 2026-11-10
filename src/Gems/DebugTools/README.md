# DebugTools

Debug lines and windows over what the meters measure, drawn with the debug UI. It provides nothing and needs a gem that
provides `IDebugUI`.

To use it, list `DebugTools` in a project's `.magic` file.

- **Counter**, on F1 or `set DebugTools.counter=true|false`: the drawn frame rate and frame time, then the GPU's memory and
  the process's, as a line in the top left corner. It shows from the start in a Debug build. In other builds the debug
  UI shows lines only while a debug window is open.
- **Stats**, on F3 or `set DebugTools.stats=true`: every instrument of the `Magic.*` meters and .NET's `System.Runtime`,
  in sections that fold. **CPU** shows the game thread's frame and the render thread's, then what Core's own steps and each gem's hooks take
  of it, each a fold of its own, the slowest first. **GPU** shows what the GPU provider says of the API and the GPU, its frame and its
  passes. **Memory** shows the process's and the GPU's, then Core's and the process's instruments, and each gem's.
  **Other** holds the rest, by meter. A histogram shows its mean, how many a second and its most, a counter how much it
  rose and how much a second, a gauge its latest value.
- Numbers are collected twice a second, and only while one of them shows.
- Without a gem that provides input, no key works.

## Measuring

Core and the gems publish what they measure on their own `System.Diagnostics.Metrics` meter: Core on `Magic.Core`, a
gem on `Magic.Gems.<Gem>`, its instruments named `magic.gems.<gem_snake_case>.*`, time in seconds and memory in bytes.
A gem's meter is made with it and disposed with it. Record a gauge each frame rather than make an observable one,
and time work into a histogram of seconds with its `Start` and `RecordSince`, which do nothing while nothing listens.
Core's console command `stats [category] <seconds>` also logs them, as text, every so many seconds, with or without
this gem: `stats gpu 30` logs those with gpu in their name.

- Core times each gem's hooks and its own steps; a gem needs nothing more to show under CPU.
- An instrument named `gpu.*` shows under GPU, with its tags' values as rows of a fold of its own, and the meter's tags
  above it.
- An instrument in bytes shows under its gem in Memory; one named `memory.usage` is the gem's total, and
  `gpu.memory.usage` also shows in the counter.
