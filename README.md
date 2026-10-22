# Magic

A modern, cross-platform, plugin-driven game engine in C# where nothing is glued in, in the spirit of Our Machinery
and Bevy. `Magic` is the engine: a host that loads a project and its gems (plugin dlls) and runs the frame loop, a
standalone program that works with or without an editor. The ECS, windows and input, rendering and physics all live
in gems, the engine's plugins.

Everything runs on Windows and Linux from one code base, on current .NET, and is written for performance; the rules
are in the [code standards](CODESTANDARDS.md). macOS is planned but not yet supported: the code keeps to what runs
there, so it can follow.

## Building and running

### Requirements

- The .NET 10 SDK; `global.json` names the version, and a later feature band of it serves.
- git, CMake, Ninja and a C++ toolchain: the native libraries are fetched and built by the first build, from their
  repositories or what their authors publish ([Native libraries](src/Bundled/Gems/README.md#native-libraries)). On
  Windows the toolchain is Visual Studio with its "Desktop development with C++" workload, which brings CMake and Ninja
  with it; on Linux, gcc or clang with the platform library's build dependencies (the X11 and Wayland development
  packages); on macOS, the Xcode command line tools, and Python 3, which the shader compiler's own build needs there,
  as it is built from source.
- A GPU with a driver for Vulkan 1.3, which the engine renders through, and the Vulkan SDK for its validation layer,
  which a setting turns on.
- PowerShell for the scripts under `tools/`: `pwsh` on Linux and macOS, and Windows PowerShell 5.1 serves on Windows.
- For reviewing an agent's change ([AGENTS.md](AGENTS.md#finish)), the `plannotator` command
  ([Plannotator](https://plannotator.ai)); building and running do not need it.

### Building

Configurations: `Debug` (integer overflow checked), `Optimize` (optimised, full symbols), `Release` (optimised, no
symbols). The GPU provider presents on the display's refresh by default only in `Release`; the others draw uncapped,
so the frame rate they show is the true one.

```powershell
dotnet build Magic.slnx
dotnet test Magic.slnx
dotnet run --project samples
```

The first build fetches the native libraries into one folder per machine, under the user's local application data
(`MagicNativeLibs` moves it), builds them for this machine, on half its cores unless `MagicNativeCores` says how
many, and generates their C# from the headers ([Native libraries](src/Bundled/Gems/README.md#native-libraries));
after that a build touches them only when one's item changes, and every checkout and worktree on the machine shares
them. The build says which library it is on as it goes, in an IDE's build output too; in a terminal, `dotnet build
-tl:off` shows it, since the terminal logger shows a task's messages only after its project ends. An IDE's background
builds, which only feed its editor, never build a library or generate its API, so the editor knows the API from the
first real build on. Visual Studio restores the packages as it loads or reloads the projects rather than when a build
starts, since a build's restore fails on a project still loading (`nuget.config`): in a fresh clone, start the first
build once that restore, in its status bar, has finished.

Running the samples, with `dotnet run --project samples` or the Samples project's Run in an IDE, first builds what
they run with: Magic, the bundled gems and resources, and the samples' own code, each only where something changed.
Then it starts Magic on them, passing it any options after `--`. Building `src/Core` alone builds Magic alone. Only a
`Release` build copies the engine's and the gems' assets beside Magic, and a project's `.magic` file and `Assets/`
beside its code, then bakes the project's caches there by running Magic on it, so its folder is a game that makes
nothing on a player's first run ([Building for Magic](src/Core/Sdk/README.md)); the bake needs a GPU, and
`-p:MagicBake=false` leaves it out. The other builds read assets where they are in the repository, so building stays
fast and an edit to one reaches a running engine at once.

### Your own gem or project

A gem or a game of one's own lives anywhere on disk and builds for a Magic folder, this repository's build or a
release ([Building for Magic](src/Core/Sdk/README.md)). `tools/InstallTemplates.ps1` installs the templates for both,
`dotnet new magic-gem` and `dotnet new magic-project`, which an IDE's New Project lists too, and points them at this
repository's build ([Tools](tools/README.md#templates)).

### Command line

`Magic --help` lists the options. `--project` takes a `.magic` file or the folder holding one, and `--frames <n>`
stops after n frames; closing the window stops sooner, as does a console gem's `quit` command typed in the terminal.
`--seconds <n>` stops once the run has gone on n seconds, however many frames that is: a ceiling for a run that
should have stopped sooner, as frames take no fixed time, where a run that must stop at the same point every time
counts frames.
`--headless` is for runs without a window: gems open none, and Ctrl+C stops the run cleanly, ending its gems, where
otherwise it ends the process at once. `--set Gem.setting=value`, as many times as needed, overrides a gem's setting
from the presets, and `--preset Name` applies a preset, an asset of such settings for a kind of machine, over the
project's own and under `--set` ([Gem settings](src/Core/README.md#gem-settings)). `--verbose` also logs the
verbose lines, detail that is usually noise. `--bake` opens each of the project's domains in turn, each once the last
is shown, then stops, so everything they use is made and cached: what a `Release` build runs to bake a project.

Exit codes: 0 clean, 1 bad arguments or a project that cannot be opened, 2 an error was logged under
`--fail-on-error`. A crash ends the process with the OS's crash code and leaves a `.crash` file in `Logs/` beside
Magic ([Crashes](src/Core/README.md#crashes)). What is slow to make, such as a compiled shader or a model's clusters,
is kept in one folder per machine, shared by every checkout and worktree, `Magic/Cache/` under the user's local
application data unless `MagicCache` names another, and made again when what it was made from changes; deleting the
folder is always safe. A project baked by a `Release` build brings its own in its folder, read first
([The cache](src/Core/README.md#the-cache)).

## How it works

Core is a thin coordinator, and [its README](src/Core/README.md) explains every mechanism it provides. In short:

- **A project** is a `.magic` file naming its entry point and its gems, its `Assets/` folder, and a csproj beside the
  file that compiles the scripts and components in `Assets/` into the project's own code
  ([The project file](src/Core/README.md#the-project-file)).
- **Gems** are the plugins. Each loads into its own `AssemblyLoadContext`, is made from what its constructor asks
  for (Core's services, interfaces other gems provide, its own settings object) and provides the Core interfaces it
  implements. Gems never reference each other's code: they talk only through interfaces in Core, so any gem can be
  swapped for another that implements the same ones ([Gems](src/Core/README.md#gems)).
- **A frame** runs on two threads. The main thread runs the game's hooks in the order the gems were made, the render
  thread draws the frame before while the main thread goes on to the next, and the two meet once per frame
  ([The frame](src/Core/README.md#the-frame)).
- **Failures** are explicit and never stop the engine: a gem that cannot be made, or that throws in a hook, is logged
  as an error and the run goes on without it. Only state that cannot be stops it, with a crash file
  ([The log](src/Core/README.md#the-log)). What the engine measures is published as .NET metrics that gems listen to
  ([Telemetry](src/Core/README.md#telemetry)).

## Folder structure

```
(root)                     the solution, and the build settings and code rules every project shares
src/Core/                  Magic: the host, the frame loop, and the interfaces and data gems share
src/Bundled/Gems/<Name>/   the gems that ship with Magic, one project each
src/Bundled/Resources/     the engine's built-in default assets, as one project
samples/                   sample projects, each a .magic file, its Assets/ (data, settings and scripts), and a csproj
tests/                     tests, one project per project tested
tools/                     what the build runs, such as the analyzers, and the scripts that check a change; nothing here ships
Build/                     build output, not in git: bin/ and obj/ by configuration, and what the tests write
```

Each area has a README explaining how it works. Core files its code by kind, one folder and namespace each, and a gem
is flat ([code standards](CODESTANDARDS.md#folders)). New engine work goes in a gem; Core gets only what gems share.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code standards](CODESTANDARDS.md).
