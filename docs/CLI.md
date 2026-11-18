# Command Line

Magic takes commands on its command line and from the terminal while it runs. This doc gives the commands of a run,
the output of Magic and its exit codes. `--help` lists the commands and the options of the command line
([Build and run](../README.md#build-and-run)).

## Supported Commands and Options

While Magic runs, you can type a command into the terminal. A typed command is the same as a command on the command
line. A typed option can omit the `--` at its start.

### The run

- `open <path>` opens a project and `headless false` shows a window. Each of them restarts the run.
- `exit` ends the run.

### Entities

`query <path>` reads the values of an entity, and `set <path> <value>` changes them.

- A path starts with the tag or the id of the entity, never with its name.
- A tag is case sensitive. `set sky.sky.brightness 2` finds the entity tagged `sky`, and
  `set Sky.sky.brightness 2` finds nothing.
- Each next part of the path is a name or its initials, as in `set player.camera.fov 100` or
  `query player.transform`.
- A path that ends at a component sets the whole component from JSON, as in
  `set player.camera '{"fieldOfView":70}'`. Put the JSON in single quotes, which keep its double quotes.
- A doubled quote inside a quoted word is one quote of the word.

### Settings and properties

- A setting of a gem has the path `Category.Member`, as in `set Rendering.Resolution 2560 1440`.
- `query settings` lists all the settings, and `query Rendering` lists one category.
- `save settings` keeps the settings that are not at their defaults, for this user. Magic opens the project with
  them from then on.
- At launch, `--configure` sets settings over the saved values, as in
  `--configure Window.Mode=windowed Rendering.Resolution="1280 720"`.

Magic keeps the saved settings and the saves of a project under the local application data folder, in
`Magic/<project>/Settings/` and `Magic/<project>/Saves/`.

A part of a gem can give its properties a path. These values are state of the run, and Magic does not save them.

- The window gives the path `Window`. `set Window.Title "my run"` names the window while the run goes on, and
  `query Window.Width` reads its width.
- Magic gives the path `World`, with `IsPaused` and `TimeScale`, as in `set World.TimeScale 0.5`.
- Magic gives the path `App`, with `ShouldExitOnError` and `Verbosity`. It also has `IsHeadless`, which you can only
  read.
- The shared gem of the samples gives the path `Day`. `set Day.Hour 12` holds the sun of each day cycle at that
  hour, and the moon 12 hours from it.

### The world

- `open <domain>` opens a domain of the project that runs, by its path or its name, in place of the world. It puts
  the player at the spawn of the domain.
- `set player.transform.position 10 2 5` moves the player.
- `spawn <prefab> [x y z]` puts a prefab, by its path or its name, at the position that you give. Without a
  position, it puts the prefab a few metres in front of the player. It prints the ids of the entities that it made
  as JSON.

### Assets

- `bake [path]` loads an asset by its path and caches what the importers and the gems make of it.
  - With a folder, as in `bake Materials`, it loads each asset under the folder.
  - Without a path, it loads every asset.
  - The lines after it wait until it ends. Thus `bake`, then `exit`, fills the caches and stops.
- `import <file> [folder]` copies a file into the project as an asset, as in `import C:/Art/Bark.png Textures`.
  - It gives the copy its `.meta` as `new handle` does, and loads the asset one time.
  - It prints the id, the path and the type of the asset as JSON.
  - Without a folder, the copy goes into the folder of the project.
  - If a file with that name is there, the import stops. With `--replace`, the import writes over the file, which
    keeps its `.meta` and thus its id.
- `export <asset> <file>` loads an asset by its path or its id and writes it to a file, as in
  `export Textures/Bark.png C:/Art/Bark.png`.
  - The name of the file ends in the extension of the type.
  - If no gem of the project writes the type, the export fails and gives the reason.
  - The file of a prefab has the handle of each prefab that it places.
  - The file omits a value that holds the default of its type. With `--defaults`, the file has every value.
- `new handle <file>` gives a `.meta` to a file that has none, with the next free id in the folder of the file.
  Thus the ids of a project, of a gem and of Magic stay apart. A file that has a `.meta` keeps its id.

### Queries

`query <subject>` prints what the run knows about a subject, as one JSON object. With `--out <file>`, it writes the
JSON to the file.

The subjects are `domain`, `components`, `asset`, `settings`, `stats`, `log`, `world` and `gems`, and each subject
that a gem adds, such as `sky` and `gpu`. A first word that is not a subject is a path that `set` takes. `query <path>`
prints the settings of a category or a key, or the value on each entity that the path names.

- `query stats [category]` measures for one second and prints each instrument. With a category, as in
  `query stats gpu`, it prints each instrument with the category in its name. With `--every <seconds>`, it logs
  the instruments at that interval, and `--every 0` stops the log.
- `query log` prints the path of the log file of the run, and how many warnings and errors the run logged.
- `query world` prints if the world loads or is paused, how fast its time runs, its domains and how many entities
  it holds.
- `query gems` prints each gem that the run started.
- `query gpu` prints the GPU that draws. The GPU provider adds this subject.
- `query components` prints each component type that the run knows, from Magic and from each loaded gem. It gives
  the name, the type and the default value of each field, and the same for a material and its params. A tool reads
  the defaults from here, because `query domain` omits them.
- `query asset <path>` prints the id, the path and the type of an asset, by its path under its project or by the
  name of its file. `query asset --id 300124` prints the same from an id.

#### `query domain`

`query domain [--names <name>...] [--tags <tag>...]` prints the open domain as the engine resolves it. A tool that
draws the same scene reads this file, not the asset files. The JSON has:

- The project.
- Each entity with its id, its name, its components and the material of each slot of its renderer.
- Each model that a renderer draws.
- For each handle, the id, the path and the type of its asset.

These rules apply to the values:

- The JSON omits a field that holds what a new value of its type holds, as an exported file does. A colour without
  `r` has an `r` of 0, also in a light whose own default colour is white.
- `base` holds what the prefab of the entity has, where the live world has a different value. An example is a value
  that a script moves.

These options select the entities:

- `--names` keeps only the entities whose name starts with one of the names, as in
  `query domain --names ReferenceView Cabin --out world.json`.
- `--tags` keeps only the entities that hold one of the tags, as in `query domain --tags render-reference`.

#### `query sky`

`query sky` prints three things. The sky gem adds this subject.

1. Each directional light of the world, with its rotation, the direction towards it, its colour and its illuminance
   in lux above the air.
2. The light that lights the world, by the rule of the renderer.
3. Each factor of the light of the sky:
   - The brightness of the sky entity. It scales the sky that a surface sees.
   - The setting `Rendering.SkyBrightness`, the tint of the sky and the red of the light. They scale the open sky
     that the air and the clouds take.

The lights are as the world holds them at the call. To read a different hour, first hold the day cycle at it
(`set Day.Hour 12`), or set the rotation of each light. Then call `query sky`.

## Output and exit codes

- Standard output holds only the result of a command, for example the values from `query` or the path of a screenshot.
  The log, the progress bars and the prompt go to standard error.
- The exit code is `0` for a clean exit. It is `1` for a bad command line, a command that failed or a project that
  does not open. It is `2` at the first error that Magic logs under `--fail-on-error`.
- Each run writes its log to a file of its own, `Logs/<time>_<process id>.log` next to the program. The file has
  each line that the terminal shows. Thus it has the verbose lines only with `--verbosity detailed`. The folder
  keeps the files of the last 20 runs.
- After a crash, Magic writes `Logs/<time>.crash` next to the program.
