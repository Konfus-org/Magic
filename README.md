# Magic

Magic is a game engine in C#. A project adds parts to the engine as plugins, which Magic calls gems. This repository
holds the `Magic` program, the gems that come with it, the sample projects and the build rules.

## Requirements

- The .NET SDK that `global.json` names.
- PowerShell for the scripts in `tools/`: `pwsh`, or Windows PowerShell 5.1 on Windows.
- To build the native libraries: git, CMake, Ninja and a C++ toolchain. On Windows, Visual Studio with its
  "Desktop development with C++" workload gives CMake, Ninja and the toolchain.

## Build and run

Do these steps in a terminal in the root folder of the repository.

### Build

Build the solution:

```powershell
dotnet build src/Magic.slnx
```

The first build also builds the native libraries, so it takes some minutes. The build puts Magic in
`Build/bin/<Configuration>/`.

### Run

1. Open a project with the built program. This command opens a sample:

   ```powershell
   Build/bin/Debug/Magic open samples/MountainCabin/MountainCabin.magic
   ```

   A window shows the project.
2. To end the run, close the window or type `exit` into the terminal.

A project's own code, such as its scripts, is the csproj next to its `.magic` file. Magic loads the build of that code
from `Projects/<csproj name>/` beside Magic. The code can use the types of Magic and of the gems that the project lists.

When you build a gem that the open project lists, or the project's code, Magic restarts the run with the new build. The
world stays where it was.

### Other supported commands

`open` is one of the commands of Magic. `--help` lists the commands and the options of the command line:

```powershell
Build/bin/Debug/Magic --help
```

[Command Line](docs/CLI.md) gives the commands that you can type while Magic runs, the output of Magic and its exit
codes. The code of the commands is in [`Commands/`](src/Core/CommandLine/Commands/).

## Folders

| Folder | Holds |
| --- | --- |
| `src/Core/` | The engine: the `Magic` program and all the parts that it is made from |
| `src/Gems/` | The gems that come with Magic ([Gems](docs/GEMS.md)) |
| `samples/` | The sample projects |
| `tools/` | The scripts, the templates and the analyzer ([Tools](docs/TOOLS.md)) |
| `docs/` | The docs for contributors |
| `Build/` | The build output. It is not in git. |

## Samples

A sample is a project that shows what Magic does. Each sample has a folder in `samples/`, with its `.magic` file and
a csproj with its own code. `tools/Scripts/Validate.ps1` runs each sample.

- To open a sample headless, run `dotnet run --project samples/<Name>`. This command builds Magic first. To
  end the run, type `exit`.
- To open a sample with a window, do the steps of [Build and run](#build-and-run) with
  `samples/<Name>/<Name>.magic`.
- To make a sample, use the project template ([Templates](docs/TOOLS.md#templates)).

Each sample lists the gem in `samples/Shared/`. This gem gives the assets that the samples share, in its `Resources/`
folder. It also gives the player prefab, which each `.magic` file names as its `player`. The player is one entity
tagged `player`, with a transform, a camera and a spin that turns it in place. A domain puts the player at its entity
tagged `spawn`.

## Contributing

Read [Contributing](docs/CONTRIBUTING.md) first. The other docs are the [Code Standards](docs/CODE_STANDARDS.md),
[Gems](docs/GEMS.md), [Command Line](docs/CLI.md), [Tools](docs/TOOLS.md), [Debugging](docs/DEBUGGING.md) and
[CI](docs/CI.md).
