# Core

Core is the `Magic` program and the library every gem compiles against. It opens a project, loads its gems, runs the
frame loop and ends the gems. Everything else, such as rendering, physics and input, is a gem. Each public type's doc
comments describe its members.

## The run

Magic opens the project's `.magic` file, makes the gems, opens the project's first domain and runs frames until the
game ends. Then each gem's `Stop` and `Dispose` run, the last gem made first. `Magic --help` lists the command-line
options.

The run ends when a gem calls `App.Quit`, the `--frames` or `--seconds` limit is reached, or Ctrl+C is pressed in a
`--headless` run. `App.Restart` restarts the run in the same process, with the world as it was. A gem that
can't be made, or throws in a hook, is logged as an error, and the run goes on without it.

## The frame

Each frame runs on two threads. The main thread runs every gem's `Update`, `FixedUpdate` (once per fixed step),
`LateUpdate` and `Sync`. The render thread then runs `Render` for that frame while the main thread starts the next.
Gem constructors and `Dispose` also run on the render thread. A gem implements only the hooks it needs
([`IGem`](Interfaces/IGem.cs)). `Sync` runs while the render thread is idle. Prepare what `Render` reads in
`LateUpdate`, into a copy `Render` isn't using, and swap the copies in `Sync`. Main-thread hooks run in the order the
gems were made, and `Render` in reverse.

### Threads

`Threads` runs work on the main or render thread from any thread, and returns a task that ends once it has run. `IGpu`
belongs to the render thread. `World`, `IEcs`, `IInput`, `IWindow` and `IDebugUI`'s widgets belong to the main thread,
but opening a domain, spawning an asset that must load first and saving the world work from any thread.

### Events

`Events` sends messages between gems. Publish a struct from any thread, and every watcher of its type gets it on the
main thread at the start of the next frame. Watching returns a `Subscription`; dispose it to stop.

## What a gem can ask for

A gem gets what it needs as constructor parameters, and keeps it: Core's services, such as `App` (the project and
quitting), `Events`, `Assets` and `World`; interfaces other gems provide, optional when the parameter has a default value; its own settings class
([Gem settings](#gem-settings)); and `IServiceProvider`, only to make objects at run time.

Every other interface the gem's class implements is what it provides. Only one gem can provide each interface, except
`ILogSink` and `IPreloader`. A gem that provides `IEcs` or `IScripts` can't take `World`.

## Gems

A gem is an assembly in `Gems/<Name>/` next to Magic, with exactly one class that implements `IGem`. It loads with its
own dependencies and talks to other gems only through Core's interfaces. Gems are made in dependency order, then in
the order the project lists them, and don't reload while Magic runs. A gem's own assets go in its `Resources/` folder.

To make one, start from the `magic-gem` template ([Templates](../../tools/README.md#templates),
[Building for Magic](Sdk/README.md)). To call a C library, see
[Native libraries](../Bundled/Gems/README.md#native-libraries).

## The project file

A project is a `.magic` file, an `Assets/` folder and an optional csproj for its code. The `.magic` file is JSON that
names the project, the domain to open first, an optional loading domain and player prefab, and the gems to run, as
`Name` or `Name@1.2` for version 1.2.x. The keys are the `Project` type's properties, and `samples/Samples.magic` is an
example.

The project's code is its scripts and components, the `.cs` files under `Assets/`. A csproj next to the `.magic` file,
named like the project, builds them ([Building for Magic](Sdk/README.md)). Rebuilding while Magic runs reloads the
code ([Hot reload](../Bundled/Resources/README.md#hot-reload)).

## Gem settings

A gem with settings declares a class that implements `ISettings`, and takes it as a constructor parameter. Each
property is a setting: its initializer is the default and its doc comment the description. Values come in layers,
each winning over the one before:

1. The class's initializers.
2. The project's `Assets/Presets/Default.preset`.
3. A preset applied over it: `--preset Name` for `Presets/Name.preset`, or one applied while running.
4. `--set Gem.path=value` on the command line.
5. Changes made while running, until another preset is applied.

A setting's key is the gem's name, then the property's path, with dots between: `Gem.path`. A preset is a JSON asset of
values by key; the engine's [presets](../Bundled/Resources/Presets/) are examples. `Settings` reads and changes
settings by key, applies presets and lists every setting for a UI, such as the settings window (F4). Mark a setting
the gem reads only at start with `[RequireRestart]`; changing it restarts the run. `[Category("Tab/Header")]` groups a
setting in a settings UI, and `[Range]` limits a number.

## The log

`Debugging` is the log. Call it from any thread: `Verbose` (shown only with `--verbose`), `Info`, `Warn`, `Error`
(counted by `--fail-on-error`), `Critical` (ends the process) or `Assert` (ends the process when false, in every
build).

### Sinks

The terminal gets every line. To send lines somewhere else, such as a file, a gem implements `ILogSink`. A sink gets
each line in order on the log's background thread. The first sink also gets the lines logged before it was added.

### Crashes

A `Critical` line, a failed `Assert` or an uncaught exception ends the process at once. The unprinted lines, the
critical line and its stack go to a `.crash` file in `Logs/` next to Magic, and the terminal prints its path.

## Assets

An asset is a file under the project's `Assets/`, the engine's `Resources/` or a gem's `Resources/`, with a `.meta`
file next to it that holds its id ([Resources](../Bundled/Resources/README.md)). A `Handle<T>` refers to an asset by
its id. `Assets` finds assets by path, finds a type's default at `<Type>s/Default` and loads assets, from any thread.
A loaded asset stays in memory while something holds it.

An asset type implements `IAsset<T>`. `Assets` reads a type whose file is the class as JSON (`IJsonAsset<T>`) or plain
text (`ITextAsset<T>`). For any other format, a gem provides an `IAssetLoader<T>`. `Events` announces loads, reloads
and file changes (`AssetLoaded<T>`, `AssetReloaded<T>`, `AssetsChanged`). `Json` reads and writes JSON in the
engine's dialect ([Reading an asset](../Bundled/Resources/README.md#reading-an-asset)).

## The cache

`Caches` stores results that are slow to make, such as compiled shaders, for later runs. A gem reads an entry by key
and version, and on a miss makes the result and writes it. The gem provides an `ICache<T>` that writes and reads the
type.

Entries live in `Magic/Cache/<project>/` in local app data, shared by every copy of Magic on the machine. Set the
`MagicCache` environment variable to use another folder. Deleting the cache is safe. A `Cache/` folder in the
project's own folder is read first and never written; a `Release` build bakes one ([Building for Magic](Sdk/README.md)).

## Components

A component is an unmanaged struct that implements `IComponent`. Declare one in a gem or the project's code, and files
name it by its type name. The samples' `Assets/Components/` has examples. `Transform` holds an entity's position, in
metres. A `tags` list on an entity gives it a `Tags` component; the engine uses `spawn` for where a domain puts the
player, `player` for the player's camera, and `global` for an entity no domain owns
([The world](#the-world)).

## Scripts

A script is a `.cs` asset whose class, named like the file, implements `IScript`. A prefab lists an entity's scripts
with their values, and each spawned entity gets its own copy. Add or remove a handle in an entity's `Scripts`
component to attach or detach one. The samples' `Assets/Scripts/` has examples.

- The constructor takes the entity first, if it uses it, then the services it needs, and only stores them.
- Public settable properties are the values, and their initializers the defaults. `Start()` runs once they're set.
- `IBehavior` adds optional `Update`, `FixedUpdate` and `LateUpdate` hooks. A script that is also a system, such as an
  `ISystemScript<T>`, runs in the ECS while its entity lives.
- An `IPipelineScript` draws part of a frame where a pass of kind `script` names it. It isn't attached to an entity.
- `[Impatient]` scripts, such as a loading domain's, run while the loading domain shows. Every other script, and every
  script in the domain being readied, runs from the frame after that domain is shown.

## The world

`World` is the game world: entities, components, queries, systems and domains. Spawn an asset, such as a `Prefab`, at
a transform to get its entities; a type that can be spawned implements `ISpawnable`. Saving writes the whole world as
a `WorldState`, and restoring puts it back.

A domain is a folder with a `.domain` file and the prefabs it spawns
([Domains](../Bundled/Resources/Domains/README.md)). It loads fully, then spawns in one frame. Opening a domain in
place of the open ones shows the project's loading domain until it is ready, then puts the player at the entity tagged
`spawn`. Closing a domain destroys what it spawned.

`World.Stream` says, each frame, where the world is in having a domain take its place. It is `Loading` while the
loading domain shows, `Readying` while the new domain is drawn out of sight, and `Shown` on the frame it appears. It
also lists the prefabs the domains spawn, and those still being loaded. The world empties as `Loading` and `Readying`
begin: every entity no open domain spawned is destroyed, including what scripts made, and each gem forgets what it
built up from the old world, such as cached lighting. A global outlives this: make one with `World.CreateGlobal`, and
list them with `World.GetGlobals`. Restoring a saved world destroys the globals too.

A gem that prepares what spawns, such as uploading models, implements `IPreloader`. It reads `World.Stream` in its
hooks, and the world asks it each frame how far it has what is being loaded and whether the domain being readied is
ready.

## The GPU

`IGpu` is the GPU, one level above the graphics API. Use it on the render thread, in `Render`, to make buffers,
textures and pipelines and record commands. A render target is a `GpuTexture`: one a gem made, or a window's.
`IShaderCompiler` compiles HLSL, and `#include "path"` loads the shader asset at `Shaders/path`
([Shaders](../Bundled/Resources/Shaders/README.md)).

A renderer draws the world from components, such as `Camera`, the lights and `PostProcessing`. A `Pipeline` asset is an
ordered list of `Pass`es and other pipelines, and a `Pass` is one step of a frame: its stage, shader and the resources
it uses by name. The engine's `Pipelines/` and `Passes/` are examples. Publish `DebugViewChanged` to show a debug view
in place of the finished image.

## Physics

An entity with a `Transform` and a `Physics` component is a body, sized from its model. A physics gem simulates it each
fixed step. Write its transform to move it, or its velocity to throw it. Add a `Contacts` component to get the bodies
it touched each step. `IPhysics` finds the nearest body along a segment, and the bodies near a point.

## Audio

An entity with a `Sound` component plays the `Audio` asset it names. Write the `Sound` to play, pause, stop, seek or
change it. A sound in the world is heard from the player's camera. A sound set to play everywhere, such as music,
sounds the same anywhere. An `Acoustics` component next to a `Physics` says what a body is made of, for an audio gem
that simulates how sound travels.

## The debug UI

`IDebugUI`, which a UI gem provides, shows information for the developer over the game. Its reports work from any
thread: text in the world or on screen shows for the frame it is called in, and an info, warning or error shows for a
while and is logged too. Its widgets draw immediate-mode windows on the main thread, in a gem's `Update`. The bundled
debug windows open with `` ` `` (console), F3 (telemetry) and F4 (settings).

## Telemetry

The engine publishes measurements as .NET metrics (`System.Diagnostics.Metrics`). A gem records on a `Meter` named
`Magic.Gems.<Gem>`, and Core on `Magic.Core`. Name instruments as OpenTelemetry does, lowercase and dotted, starting
with the meter's name in snake case (`magic.gems.<gem>.`); times end in `.duration`. Make a time's histogram with
`meter.CreateTimer`, and time with `Start` and `RecordSince`, which measure only while something listens. To read
them, use a `MeterListener`, OpenTelemetry's exporters or `dotnet-counters`. A listener's callback runs on the thread
that recorded and must not block. Core times each frame, each gem's hook and each asset load; the names are constants
on `Metrics`.
