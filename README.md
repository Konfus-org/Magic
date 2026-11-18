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

A project's own code, such as its scripts, is the csproj next to its `.magic` file. Magic loads the build of that code
from `Projects/<csproj name>/` beside Magic. The code can use the types of Magic and of the gems that the project lists.

When you build a gem that the open project lists, or the project's code, Magic restarts the run with the new build. The
world stays where it was.

### Commands in a run

While Magic runs, you can type a command into the terminal. A typed command is the same as a command on the command
line. A typed option can leave out its leading `--`.

- `open <path>` and `headless false` restart the run.
- `query <path>` and `set <path> <value>` read and change the values of an entity. The path starts with the tag or the
  id of the entity, never with its name, and a tag is case sensitive: `set sky.sky.brightness 2` finds the entity
  tagged `sky`, and `set Sky.sky.brightness 2` finds nothing. Each next part is a name or its initials, as in
  `set player.camera.fov 100` or
  `query player.transform`. A path that ends at a component sets the whole component from JSON in single quotes, which
  keep its double quotes, as in `set player.camera '{"fieldOfView":70}'`. A doubled quote inside a quoted
  word is one quote of the word.
- A setting of a gem has the path `Category.Member`, as in `set Rendering.Resolution 2560 1440`. `query settings` lists
  all the settings, and `query Rendering` lists one category.
- A part of a gem can give its properties a path, as the window does: `set Window.Title "my run"` names the window
  while the run goes on, and `query Window.Width` reads its width. These values are state of the run and are not
  saved.
- Magic itself gives two such paths. `World` has `IsPaused` and `TimeScale`, as in `set World.TimeScale 0.5`. `App`
  has `ShouldExitOnError` and `Verbosity`, and `IsHeadless`, which you can only read.
- The shared gem of the samples gives the path `Day`: `set Day.Hour 12` holds the sun of each day cycle at that hour,
  and the moon 12 hours from it.
- `save settings` keeps the settings that are not at their defaults, for this user. Magic opens the project with them
  from then on.
- `open <domain>` opens a domain of the running project, by its path or its name, in place of the world. It puts the
  player at the spawn of the domain. `set player.transform.position 10 2 5` moves the player.
- `spawn <prefab> [x y z]` puts a prefab, by its path or name, at the position that you give. Without a position, it
  puts the prefab a few metres in front of the player. It prints the ids of the entities that it made as JSON.
- `bake [path]` loads an asset by its path, or each asset under a folder, as `bake Materials`, and caches what the
  importers and the gems make of it. Without a path, it loads every asset. The lines after it wait until it ends, so
  `bake` then `exit` fills the caches and stops.
- `import <file> [folder]` copies a file into the project as an asset, as `import C:/Art/Bark.png Textures`. It gives
  the copy its `.meta` as `new handle` does, loads the asset one time, and prints its id, its path and its type as
  JSON. Without a folder, the copy goes into the folder of the project. A file with that name there stops the import;
  with `--replace`, the file is written over it, which keeps its `.meta` and so its id.
- `export <asset> <file>` loads an asset by its path or its id and writes it to a file, as
  `export Textures/Bark.png C:/Art/Bark.png`. The file ends in the extension of the type. It fails with the reason
  when no gem of the project writes the type. A prefab is written with the handle of each prefab that it places. A
  value that holds the default of its type is left out; with `--defaults`, the file has every value.
- `query <subject>` prints what the run knows about a subject, as one JSON object. With `--out <file>`, it writes
  the JSON to the file. The subjects are `domain`, `components`, `asset`, `settings`, `stats`, `log`, `world`, `gems`,
  and each subject that a gem adds, such as `sky` and `gpu`. A first word that is not a subject is a path that `set`
  takes: `query <path>` prints the settings of a category or a key, or the value on each entity that the path names.
- `query stats [category]` measures for one second and prints each instrument, or each with the category in its
  name, as `query stats gpu`. With `--every <seconds>`, it logs them at that interval, and `--every 0` stops the log.
- `query log` prints the path of the log file of the run, and how many warnings and errors the run logged.
- `query world` prints whether the world loads or is paused, how fast its time runs, its domains and how many
  entities it holds. `query gems` prints each gem that the run started. `query gpu` prints the GPU that draws. The GPU
  provider adds that subject.
- `query domain [--names <name>...] [--tags <tag>...]` prints the open domain as the engine resolves it: the
  project, each entity with its id, its name, its components and the material of each slot of its renderer, and each
  model that a renderer draws. A field that holds what a new value of its type holds is left out, as an exported
  file leaves it out: a colour without `r` has an `r` of 0, also in a light whose own default colour is white. And
  `base` holds what the entity's prefab has where the live world has something else, such as a value that a script
  moves. Each handle in it has the id, the path and the type of its asset. `--names` keeps only the entities whose
  name starts with one of the names, as `query domain --names ReferenceView Cabin --out world.json`, and `--tags` only
  the entities that hold one of the tags, as `query domain --tags render-reference`. A tool that draws the same scene reads
  this file instead of the asset files.
- `query sky` prints three things. First, each directional light of the world with its rotation, the direction
  towards it, its colour and its illuminance in lux above the air. Second, which light lights the world, by the rule
  of the renderer. Third, each factor of the sky's light: the brightness of the sky entity, which scales the sky that
  a surface sees, and the setting `Rendering.SkyBrightness` with the sky's tint and the red of the light, which scale
  the open sky that the air and the clouds take. The lights are as the world holds them at the call. To read another
  hour, hold the day cycle at it first (`set Day.Hour 12`), or set the rotation of each light, and then call
  `query sky`. The sky gem adds this subject.
- `query components` prints each component type that the run knows, Magic's and each loaded gem's, with each
  field's name, type and default value, and the same for a material and its params. A tool reads the defaults from
  here, because `query domain` leaves them out.
- `query asset <path>` prints the id, the path and the type of an asset, by its path under its project or by its
  file's name; `query asset --id 300124` prints the same from an id.
- `new handle <file>` gives a file that has no `.meta` one,
  with the next id free in the folder that the file is in, so that the ids of a project, of a gem and of Magic stay
  apart. A file that has a `.meta` keeps its id.

At launch, `--configure` sets settings over the saved values, as in
`--configure Window.Mode=windowed Rendering.Resolution="1280 720"`.

Magic keeps the saved settings and the saves of a project under the local application data folder, in
`Magic/<project>/Settings/` and `Magic/<project>/Saves/`.

### Output and exit codes

- Standard output holds only the result of a command, for example the values from `query` or the path of a screenshot.
  The log, the progress bars and the prompt go to standard error.
- The exit code is `0` for a clean exit. It is `1` for a bad command line, a command that failed or a project that
  does not open. It is `2` at the first error that Magic logs under `--fail-on-error`.
- Each run writes its log to a file of its own, `Logs/<time>_<process id>.log` next to the program. The file has
  each line that the terminal shows, so it has the verbose lines only with `--verbosity detailed`. The folder keeps
  the files of the last 20 runs.
- After a crash, Magic writes `Logs/<time>.crash` next to the program.

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
