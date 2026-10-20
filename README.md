# Magic

A modern, cross-platform, plugin-driven game engine in C# where nothing is glued in, in the spirit of Our Machinery
and Bevy. `Magic` is the engine: a host that loads a project and its gems (plugin dlls) and runs the frame loop, a
standalone program that works with or without an editor. The ECS, windows and input, rendering and physics all live
in gems, the engine's plugins.

Everything runs on Windows, Linux and macOS from one code base, on current .NET, and is written for performance; the
rules are in the [code standards](CODESTANDARDS.md).

## Building and running

### Requirements

- The .NET 10 SDK; `global.json` names the version, and a later feature band of it serves.
- git, CMake, Python 3 with the `ply` package (`pip install ply`) and a C++ toolchain: every native library is built
  from its own repository by the first build ([Gems](src/Bundled/Gems/README.md#native-libraries)). On Windows the
  toolchain is Visual Studio with its "Desktop development with C++" workload, which brings the Windows SDK and the
  DIA SDK the shader compiler's library needs; on Linux, gcc or clang with the platform library's build dependencies
  (the X11 and Wayland development packages); on macOS, the Xcode command line tools.
- A GPU with a driver for Vulkan 1.1, Direct3D 12 or Metal, which the engine renders through.
- PowerShell for the scripts under `tools/`: `pwsh` on Linux and macOS, and Windows PowerShell 5.1 serves on Windows.

### Building

Configurations: `Debug` (integer overflow checked), `Optimize` (optimised, full symbols), `Release` (optimised, no
symbols). The GPU provider presents on the display's refresh by default only in `Release`; the others draw uncapped,
so the frame rate they show is the true one.

```powershell
dotnet build Magic.slnx
dotnet test Magic.slnx
Build/bin/Debug/Magic --project samples
```

The first build clones the native libraries into `Build/NativeLibs/` (`MagicNativeLibs` moves it), builds them for
this machine, on half its cores unless `MagicNativeCores` says how many, and generates their C# from the headers
([Gems](src/Bundled/Gems/README.md#native-libraries)); after that a build touches them only when one's commit
changes. Building `src/Core` alone builds the bundled gems and resources with it, so `dotnet run --project src/Core`
or a run from an IDE never loads a gem left stale against a new Magic; the solution build adds the samples' own code.
Run from an IDE or with `dotnet run --project src/Core`, Magic opens the samples project. Only a `Release` build
copies the engine's and the gems' assets beside Magic; the others read them where they are in the repository, so
building stays fast and an edit to one reaches a running engine at once.

### Command line

`Magic --help` lists the options. `--project` takes a `.magic` file or the folder holding one, and `--frames <n>`
stops after n frames; closing the window stops sooner, as does a console gem's `quit` command typed in the terminal.
`--headless` is for runs without a window: gems open none, and Ctrl+C stops the run cleanly, ending its gems, where
otherwise it ends the process at once. `--set Gem.setting=value`, as many times as needed, overrides a gem's setting
from the presets, and `--preset Name` applies a preset, an asset of such settings for a kind of machine, over the
project's own and under `--set` ([Gem settings](src/Core/README.md#gem-settings)). `--verbose` also logs the
verbose lines, detail that is usually noise.

Exit codes: 0 clean, 1 bad arguments or a project that cannot be opened, 2 an error was logged under
`--fail-on-error`. A crash ends the process with the OS's crash code and leaves a `.crash` file in `Logs/` beside
Magic ([Crashes](src/Core/README.md#crashes)). What is slow to make, such as a compiled shader or a model's clusters,
is kept in `Cache/` beside Magic and made again when what it was made from changes; deleting the folder is always
safe ([The cache](src/Core/README.md#the-cache)).

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
tools/                     what the build runs, such as the native bindings generator, and the scripts that check a change; nothing here ships
Build/                     build output, not in git: bin/ and obj/ by configuration, and what the tests write
```

Each area has a README explaining how it works. Core files its code by kind, one folder and namespace each, and a gem
is flat ([code standards](CODESTANDARDS.md#folders)). New engine work goes in a gem; Core gets only what gems share.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code standards](CODESTANDARDS.md).
