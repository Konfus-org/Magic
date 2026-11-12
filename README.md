# Magic

Magic is a game engine in C#. A project adds parts to the engine as plugins, which Magic calls gems. This repository
holds the `Magic` program, the gems that come with it, the sample projects and the build rules.

## Requirements

- The .NET SDK that `global.json` names.
- PowerShell for the scripts in `tools/`: `pwsh`, or Windows PowerShell 5.1 on Windows.
- To build the native libraries: git, CMake, Ninja and a C++ toolchain. On Windows, Visual Studio with its
  "Desktop development with C++" workload gives CMake, Ninja and the toolchain.

## Build and run

```powershell
dotnet build src/Magic.slnx
dotnet run --project src/Core -- open <path>
dotnet run --project src/Core -- open <path> --headless
```

The build puts Magic in `Build/bin/<Configuration>/`. The text after `--` goes to Magic. `--help` lists the commands
and options, and the commands are in [`Commands/`](src/Core/CommandLine/Commands/).

To open a project with a window, the project must list a gem that provides a window. `--headless` runs the project
without a window.

When you build a gem that the open project lists, Magic restarts the run with the new build. The world stays where it
was.

### Commands in a run

While Magic runs, you can type a command into the terminal. A typed command is the same as a command on the command
line. A typed option can leave out its leading `--`.

- `open <path>` and `headless false` restart the run.
- `get <path>` and `set <path> <value>` read and change the values of an entity. The path starts with the tag or the
  id of the entity. Each next part is a name or its initials, as in `set player.camera.fov 100` or
  `get player.transform`.
- A setting of a gem has the path `Category.Member`, as in `set Rendering.Resolution 2560 1440`. `get` alone lists all
  the settings, and `get Rendering` lists one category.
- `save settings` keeps the settings that are not at their defaults, for this user. Magic opens the project with them
  from then on.
- `teleport x y z` moves the player. `teleport <domain> [x y z]` opens a domain by its path or name. It puts the player
  at the spawn of the domain, or at the position that you give.
- `spawn <prefab> [x y z]` puts a prefab, by its path or name, at the position that you give. Without a position, it
  puts the prefab a few metres in front of the player.

At launch, `--configure` sets settings over the saved values, as in
`--configure Window.Mode=windowed Rendering.Resolution="1280 720"`.

Magic keeps the saved settings and the saves of a project under the local application data folder, in
`Magic/<project>/Settings/` and `Magic/<project>/Saves/`.

### Output and exit codes

- Standard output holds only the result of a command, for example the values from `get` or the path of a screenshot.
  The log, the progress bars and the prompt go to standard error.
- The exit code is `0` for a clean exit. It is `1` for a bad command line, a command that failed or a project that
  does not open. It is `2` at the first error that Magic logs under `--fail-on-error`.
- After a crash, Magic writes `Logs/<time>.crash` next to the program.

## Folders

| Folder | Holds |
| --- | --- |
| `src/Core/` | The engine: the `Magic` program and all the parts that it is made from |
| `src/Gems/` | The gems that come with Magic. Each gem builds to `Gems/<Name>/` next to Magic. |
| `samples/` | The sample projects |
| `tools/` | The scripts and the analyzers. Nothing in this folder ships. |
| `docs/` | The docs for contributors |
| `Build/` | The build output. It is not in git. |

## Samples

Each folder in `samples/` is a project that shows what Magic does. A sample has its `.magic` file and a csproj with
its own code. `tools/Scripts/Validate.ps1` runs each sample.

- To open a sample without a window, run `dotnet run --project samples/<Name>`. This builds Magic first. Type `exit`
  to end the run.
- To open a sample with a window, build the solution. Then run
  `dotnet run --project src/Core -- open samples/<Name>/<Name>.magic`.
- To make a sample, use the project template ([Templates](docs/TOOLS.md#templates)).

Each sample lists the gem in `samples/Shared/`. This gem gives the assets that the samples share, in its `Resources/`
folder. It also gives the player prefab, which each `.magic` file names as its `player`. The player is one entity
tagged `player`, with a transform, a camera and a spin that turns it in place. A domain puts the player at its entity
tagged `spawn`.

## Contributing

Read [Contributing](docs/CONTRIBUTING.md) first. The other docs are the [Code Standards](docs/CODE_STANDARDS.md),
[Gems](docs/GEMS.md), [Tools](docs/TOOLS.md), [Debugging](docs/DEBUGGING.md) and [CI](docs/CI.md).
