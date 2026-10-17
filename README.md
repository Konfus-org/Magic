# Magic

A modern, cross platform, plugin-driven game engine in C# where nothing is glued in, in the spirit of Our Machinery and Bevy.
`Magic` is the engine: a host that loads a project and its gems (plugin dlls) and runs the frame loop, a
standalone program that works with or without the editor. The ECS, windows and input, and rendering all live in
gems (`src/Bundled/Gems`), the engine's plugins.

Everything is cross-platform (Windows, Linux, macOS planned), modern .NET, and written for performance; the rules
are in the [code standards](CODESTANDARDS.md).

## Building and running

Needs the .NET 10 SDK. Configurations: `Debug` (integer overflow checked), `Optimize` (optimised, full symbols),
`Release` (optimised, no symbols). Only a release presents on the display's refresh by default; the others draw
uncapped, so the frame rate they show is the true one.

```powershell
dotnet build Magic.slnx
dotnet test Magic.slnx
Build/net10.0/Debug/bin/Magic --project samples
```

Building `src/Core` alone builds the bundled gems and resources with it, so `dotnet run --project src/Core` or a run
from an IDE never loads a gem left stale against a new Magic; the solution build adds the samples' own code. Run from
an IDE or with `dotnet run --project src/Core`, Magic opens the Samples project. Only a `Release`
build copies the engine's and the gems' assets beside Magic; the others read them where they are in the repository, so
building stays fast and an edit to one reaches a running engine at once.

`Magic --help` lists the options. `--project` takes a `.magic` file or the folder holding one, `--frames <n>` stops
after n frames, and type `quit` in the terminal or close the window to stop sooner. `--headless` is for runs without
a window: gems open none, and Ctrl+C stops the run cleanly, ending its gems, where otherwise it ends the process at
once. `--set Gem.setting=value`, as many times as needed, overrides a gem's setting from the presets, and
`--preset Name` applies a preset, an asset of such settings for a kind of machine (`Toaster`, `Fancy`), over the
project's own, its `Presets/Default.preset`, and under `--set`. `--verbose` also logs `Debugging.Verbose` lines, detail
that is usually noise. Exit codes: 0 clean, 1 bad arguments or a project that cannot be opened, 2 an error was logged
under `--fail-on-error`. A
crash ends the process with the OS's crash code and leaves a `.crash` file in `Logs/` beside Magic. What is slow to
make, such as a compiled shader or a model's clusters, is kept in `Cache/` beside Magic, and made again when
what it was made from changes; deleting the folder is always safe.

The build is strict: every compiler, analyzer, MSBuild and NuGet warning is an error. `.editorconfig` holds the
rules, `BannedSymbols.txt` the APIs the build refuses, each with what to use instead, and `Directory.Build.props`
checks that every namespace matches its folder.

## How it works

Core is a thin coordinator. `Engine.Run` does four things, in order, on the process's first thread, which the OS ties
windows and their events to and so becomes the render thread:

1. **Opens the project.** Its `.magic` file (JSON) names the project, its entry point and its gems. A key the file
   format does not know is an error, so a misspelt key fails loudly instead of being ignored.
2. **Makes the gems**, once it has loaded the project's own code, if it has any: its scripts and their components,
   which the csproj beside its `.magic` file compiles from its `Assets/` ([Core's README](src/Core/README.md#the-project-file)).
   Gem `Name` is `Gems/Name/Name.dll` beside Magic, loaded into its own `AssemblyLoadContext`
   with its own dependencies, and holds exactly one class that implements `IGem`. That class's constructor
   parameters are what it needs: Core's services, the assets among them, interfaces other gems implement, and its own
   settings object, filled from its defaults, then the project's own preset, the preset applied over it, then `--set`,
   and changed while running through Core's `Settings` ([Core's README](src/Core/README.md#gem-settings)). Every other
   interface it implements is what it provides, but `ILogSink`, which makes it somewhere the log goes. Gems are made in
   dependency order, and in the project's order where dependencies do not decide.
3. **Runs frames, on two threads.** A frame runs five phases, and each phase calls that hook on every gem that has one,
   so data flows one way from the start of a frame to its end. A main thread started beside the render thread runs the
   game's, in the order the gems were made: `Update`, `FixedUpdate` (once per fixed step), `LateUpdate` and `Sync`.
   The render thread then runs `Render`, in reverse order, while the main thread goes on to the next frame, so drawing
   and presenting never hold the game up. `LateUpdate` prepares what `Render` will read, into a copy `Render` is not
   reading; `Sync` is the one point where both hold still, and swaps the copies. Work on other threads is tasks, and
   hands what it made to the main or render thread through `Threads` ([Core's README](src/Core/README.md#threads)).
4. **Ends the gems**, in reverse order, on the render thread, once a gem stops the run (`Lifetime.Stop`), Ctrl+C is
   pressed in a `--headless` run or the `--frames` limit is reached.

Gems never reference each other. They talk only through interfaces in Core, so a gem can be swapped for any other
that implements the same ones. Failures are explicit and never stop the engine: a gem that cannot be found,
loaded or made, or that throws in a hook, is logged as an error, and the run goes on without it. Only state that
cannot be stops it: a failed assert crashes at once, leaving a crash file. The log, `Debugging`, lives in Core, so
that it works before any gem exists; any thread can write to it, and a background task prints to the terminal and to
wherever gems send it. What the engine measures, such as how long a frame takes, is published as .NET metrics that
gems listen to ([Core's README](src/Core/README.md#the-log)).

## Folder structure

```
(root)                     the solution, and the build settings and code rules every project shares
src/Core/                  Magic: the host, the frame loop, and the interfaces and data gems share
src/Bundled/Gems/<Name>/   the gems that ship with Magic, one project each
src/Bundled/Resources/     the engine's own assets, its bundled gems' among them, as one project
samples/                   sample projects, each a .magic file, its Assets/ (data, settings and scripts), and a csproj
tests/                     tests, one project per project tested
tools/                     what the build runs, such as the bindings generator, and the scripts that check a change; nothing here ships
Build/                     build output, not in git
```

Each project has a README with its details. Core files its code by kind, one folder and namespace each, and a gem is
flat ([code standards](CODESTANDARDS.md#folders)). New engine work goes in a gem; Core gets only what gems share.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code standards](CODESTANDARDS.md).

AI-assisted contributions are welcome under the same standards as any other code: you must
understand, be able to explain, and be ready to change what you submit, and a human reviews and approves
everything before it merges.
