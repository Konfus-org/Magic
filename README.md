# Magic

Magic is a plugin-driven game engine written in C#. The `Magic` program loads a project and its gems (plugins) and
runs the frame loop. Everything else, such as the ECS, windows, input, rendering and physics, is a gem.

Magic runs on Windows and Linux. macOS is planned.

## Requirements

- The .NET SDK that `global.json` names.
- git, CMake, Ninja and a C++ toolchain, for the native libraries the first build compiles.
  - Windows: Visual Studio with the "Desktop development with C++" workload.
  - Linux: gcc or clang, plus the X11 and Wayland development packages.
- A GPU with Vulkan 1.3, and the Vulkan SDK if you want its validation layer (a setting turns it on).
- PowerShell for the scripts in `tools/`: `pwsh`, or Windows PowerShell 5.1 on Windows.
- [Plannotator](https://plannotator.ai), only for reviewing an agent's change.

## Building and running

```powershell
dotnet build Magic.slnx
dotnet test Magic.slnx
dotnet run --project samples
```

There are three configurations: `Debug`, `Optimize` (optimized, with symbols) and `Release`. Only `Release` caps
the frame rate to the display.

The first build fetches and builds the native libraries, once per machine
([Native libraries](src/Bundled/Gems/README.md#native-libraries)). To watch its progress in a terminal, use
`dotnet build -tl:off`. In Visual Studio, on a fresh clone, let the package restore finish before you build.

`dotnet run --project samples` builds what changed and starts Magic on the samples. Options after `--` go to Magic.

A `Release` build bakes the project's caches next to it ([Building for Magic](src/Core/Sdk/README.md)). Pass
`-p:MagicBake=false` to skip that step.

## Your own gem or project

Run `tools/InstallTemplates.ps1`, then `dotnet new magic-gem` or `dotnet new magic-project`
([Templates](tools/README.md#templates)). A gem or project can live anywhere on disk
([Building for Magic](src/Core/Sdk/README.md)).

## Command line

`Magic --help` lists the options.

- Exit codes: `0` clean, `1` bad arguments or a project that won't open, `2` an error was logged under
  `--fail-on-error`.
- A crash writes a `.crash` file to `Logs/` next to Magic ([Crashes](src/Core/README.md#crashes)).
- Slow-to-make results, such as compiled shaders, are cached in `Magic/Cache/` in local app data. Set `MagicCache` to
  move the cache. Deleting it is safe ([The cache](src/Core/README.md#the-cache)).

## How it works

- **A project** is a `.magic` file, an `Assets/` folder and a csproj for its scripts
  ([The project file](src/Core/README.md#the-project-file)).
- **Gems** are plugins that talk only through Core's interfaces, so any gem can be swapped for another
  ([Gems](src/Bundled/Gems/README.md)).
- **A frame** runs game logic on the main thread while the render thread draws the previous frame
  ([The frame](src/Core/README.md#the-frame)).
- **Failures** are logged and the engine keeps running without the failing gem ([The log](src/Core/README.md#the-log)).

## Folders

```
src/Core/                  Magic itself: the host, the frame loop and the interfaces gems share
src/Bundled/Gems/<Name>/   the gems that ship with Magic
src/Bundled/Resources/     the engine's built-in assets
samples/                   the sample project
tests/                     one test project per project tested
tools/                     the repository's analyzers and the build and check scripts; nothing here ships
Build/                     build output, not in git
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code standards](CODESTANDARDS.md).
