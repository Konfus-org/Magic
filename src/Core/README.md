# Core

`Magic`: the host executable, and the library every gem compiles against. It opens the project, loads the gems, makes
the world, runs the frames and ends the gems; everything else is a gem. Its code is filed by kind, one folder and
namespace each, as the [code standards](../../CODESTANDARDS.md#folders) lay out: what gems share is public (the
interfaces and the services), and the host's own plumbing is internal. Something only one gem uses lives in that gem,
not here.

## The run

The engine runs on the process's first thread, which the OS ties windows and their events to, and so becomes the
render thread. It:

1. **Opens the project**, its `.magic` file ([The project file](#the-project-file)). A project that cannot be opened
   ends the run with exit code 1; a key the file format does not know is an error, so a misspelt key fails loudly
   instead of being ignored.
2. **Loads the code and makes the gems**: the project's own code first, when it has been built, then each gem
   ([Gems](#gems)), made from what its constructor asks for ([What a gem can ask for](#what-a-gem-can-ask-for)). The
   settings are made first of all, since every gem's are made from the presets.
3. **Runs frames** on a main thread started beside the render thread ([The frame](#the-frame)). Before the first
   frame, on the main thread, the world opens the project's entry point, or is put back as the run before left it.
4. **Ends the gems** once a gem stops the run through `Lifetime`, the `--frames` limit is reached, or Ctrl+C is
   pressed in a `--headless` run (with a window, Ctrl+C ends the process at once, as it does any program). Each gem's
   `Stop` runs on the main thread, in reverse order, ending what it owns of that thread's; then each gem's `Dispose`
   runs on the render thread, in reverse order, so each ends before what it uses. One that throws while ending is
   logged, and the rest still end.

A run can also restart, which `Lifetime` offers and a change to a `[RequireRestart]` setting asks for: the world is
saved on the main thread as the last frame ends ([The world](#the-world)), the gems end, and the project runs again at
once with the command line as every setting is now, the saved world and the frame clock, so the game goes on where it
was.

Failures are explicit and never stop the engine. A gem that cannot be found, loaded or made, or that throws in a
hook, is logged as an error, and the run goes on without it and without the gems that need it. Only state that cannot
be stops the run: a failed assert crashes at once, leaving a crash file ([Crashes](#crashes)).

## The frame

The engine has two threads of its own. The render thread is the process's first: start-up, gem constructors and
`Dispose`, the `Render` hooks, and so the window and the GPU. The main thread is started beside it: the `Update`,
`FixedUpdate`, `LateUpdate` and `Sync` hooks, and the ECS. The frame loop shares each frame between them:

```
main:    Update N  FixedUpdate N  LateUpdate N  (wait)  Sync N  |  Update N+1  FixedUpdate N+1  LateUpdate N+1  (wait)  Sync N+1  | ...
render:                                                         |  Render N                                                       |  Render N+1 ...
```

A frame on the main thread begins with the work handed to it ([Threads](#threads)) and the messages published since
the frame before ([Events](#events)), then `Update`, `FixedUpdate` once for every fixed step since the last frame
(none for some frames, each given that step as its delta), then `LateUpdate`. `FixedUpdate` runs at most a few
times in one frame: a frame further behind drops the rest, so the world loses that time rather than the frame
growing to make it up and the next falling further behind. Those few steps catch up an ordinary hitch, and bound what
a frame that takes them all costs. The cap cannot help a step that takes longer than the time it simulates: the loop
then runs the most it may every frame, the world falls behind real time, and the step is what must be fixed, so a
`FixedUpdate` hook has a small fraction of the fixed step, every step. The main thread then waits until the render
thread is done with the frame before (a wait the engine measures), applies the settings changed since
([Gem settings](#gem-settings)), runs `Sync`, and hands the frame to the render thread, which runs `Render` while the
main thread goes on to the next. So the render thread runs at most one frame behind, and `Sync` is the one point
where both hold still: a gem prepares in `LateUpdate` what its `Render` reads, into a copy `Render` is not reading,
swaps the two in `Sync` and nothing more, and `Render` reads only that copy.

Each phase calls only the gems that implement its hook. The main thread's hooks run in the order the gems were made,
so a gem is up to date before the gems that take it read it: the gem providing `IInput` takes in the frame's input in
its `Update`, before theirs. `Render` runs in reverse, so a gem finishes what the gems that take it drew: the gem
providing `IGpu` submits and presents in its `Render`, after every gem that records into it. Everything else is tasks
on .NET's thread pool (`Task.Run`, async code, a task let go), and an ECS gem may spread a parallel system over
threads of its own. What a task makes for a gem reaches it as data, never as work: the gem keeps what arrived, in a
channel or a reference written whole, and takes it in at the start of the hook that owns the structure it goes into, a
loaded material as the render thread's `Render` starts, a fetched script as the main thread's `Update` starts. So a
gem's hook reads top to bottom what came in and what it does with it, each arrival lands in the gem's own phase and is
timed as its hook, and no thread runs code on another's behalf.

### Threads

`Threads` runs a step of an async flow on an engine thread, from any thread, for the flow that must await it and what
it returns, as opening a domain awaits its spawning on the main thread. Work handed to the main thread runs there in
the order handed over, at the start of the next frame, before `Update`, and work handed to the render thread runs
between frames; the task ends once it has run, with what it returned or threw, and each piece is timed while
something listens, as a hook is, since it stalls the frame as a hook does. A worker that builds a level's entities
hands them to the main thread this way, in one batch. `Threads` also starts a thread of its own for work as long as
the run, such as reading the terminal, given the run's stopping token, and says whether the calling thread may do an
engine thread's work now: it is that thread, or that thread is not running.

Nothing waits for another thread but the frame loop: there is no blocking invoke, so the two engine threads never
wait on each other. Work handed to the main thread before its first frame runs then. When the frame loop ends, each
gem's `Stop` runs on the main thread, in reverse order, where it ends what it owns of that thread's (an ECS gem's job
threads, the scripts); the main thread then runs what it was left and takes no more. The render thread runs what it
was left, takes no more, and the gems are disposed on it. Anything handed to either after that is dropped.

What belongs to which thread: `IGpu` and the commands recorded into it are the render thread's; `World` (but
opening a domain, spawning an asset that must load first and saving, which any thread may call), `IEcs`, `IInput`,
`IWindow`'s size and clipboard and `IDebugUI`'s widgets are the main thread's. A provider may throw when called from
the wrong one.

### Events

`Events` is how gems tell each other what happened. Anything publishes a message, a struct, from any thread; every
watcher of its type, which keeps its watch as a `Subscription`, hears it on the main thread at the start of the next
frame, after the work handed to the main thread and before `Update`, in the order the messages were published.
Publishing allocates nothing: each type has a queue of its own, and a message nobody watches goes nowhere. A watcher
that throws is logged, and the others still hear the message; what a watcher publishes waits for the next frame.

## What a gem can ask for

Core registers its services, every gem and the interfaces each gem provides with a service provider
(Microsoft.Extensions.DependencyInjection, private to Core), which fills a gem's constructor parameters. A gem can
ask for:

- **The run**: `Lifetime`, which stops or restarts it; `Threads`; `Events`; `Args`, the command line, as a platform
  gem reads to open no window when headless; and `Project`, the project file as read.
- **The disk and the data**: `IFileSystem`, `Assets`, `Caches` and `Settings`.
- **The code loaded**, `Assemblies`: where the type a file names, a component's struct or a script's class, is found
  by its name.
- **The world**, `World`. A gem that provides what the world is built on, the ECS or the scripts, cannot take it: the
  world is made over them, and the loader reports the cycle. What such a gem needs of the world reaches it as data, as
  the gem that runs scripts hears on `Events` that the world is loading rather than asking the world.
- **An interface another gem provides**, which is made first. A parameter with a default value is optional: when no
  gem provides it, the gem gets the default. A gem that provides it but fails to start takes the gems that need it
  down too, optional or not.
- **Its own settings**, an `ISettings` ([Gem settings](#gem-settings)).

A gem takes what it needs in its constructor and keeps it; nothing asks the provider for a service later
([code standards](../../CODESTANDARDS.md#gems-systems-and-scripts)). The one exception is `IServiceProvider` itself,
for a gem that makes objects at run time from their constructors' parameters, as the gem that runs scripts makes each
script from the services its class asks for. Core's own services find the interfaces gems provide for a type (an
`IAssetLoader<T>`, an `ICache<T>`) the same way.

Every other interface a gem implements is what it provides, and one gem provides each, but two: `ILogSink`
([Sinks](#sinks)) and `IPreloader` ([The world](#the-world)) are not provided to other gems, so any number of gems
can be sinks or preloaders.

## Gems

A gem is loaded from its own folder beside Magic, named for it, into its own `AssemblyLoadContext` with its own
dependencies, so two gems can use different versions of a package, and holds exactly one class that implements
`IGem`. Its csproj says who made it, what it is and its version, which the log names as it starts; a project that
needs a particular version lists the gem with it, and any other version is not made. Gems are made in dependency
order, and in the project's order where dependencies do not decide. A gem is never loaded again while the engine
runs: a gem takes a restart.

Gems never reference each other's code. They talk only through interfaces in Core, so a gem can be swapped for any
other that implements the same ones.

A native library is the one thing gems share directly. Two copies of a library in one process would each keep their
own state, so a GPU gem could not draw into a window the platform gem's copy opened; the gem's load context therefore
loads each library once, from the folder of the first gem that binds it, and hands every gem that binds the same name
after that copy. One project declares each library and the gems that bind it reference that project, so every gem
that binds a name expects the same one; a project that holds only a library's generated API and no `IGem` is how
several gems share a library. A library is found by the gem's dependency file, which lists what its packages
brought, or else under the gem's native runtime folder, where the build puts the binary a gem declares
([Sharing a library](../Bundled/Gems/README.md#sharing-a-library)).

## The project file

Core finds a project's `.magic` file (the path given, or the one `.magic` file in that folder) and reads it into the
`Project` service. The file names the project, the domain it opens with, the domain shown while another replaces the
world (without it, the engine's own loading domain), the prefab put at a domain's spawn when it replaces the world
(the player, whose camera is tagged as the player; none by default), and the gems to run, by their folder names, each
with the version it must be when that matters ([Gems](#gems)). What a gem depends on is what its constructor asks
for, so that is not listed. The project's root is the folder the file is in, set by Core, never a key in the file.
The keys are the `Project` type's properties, documented on it, and the samples' project file is the example.

A project's own code is its scripts and the components they use: the `.cs` files under its `Assets/`, which a csproj
beside the `.magic` file, named like the project, compiles into a folder of its own beside Magic (the samples' build
properties set that up). It is no gem and is not listed: Core loads it, when it has been built, into a load context of
its own as it loads a gem, and takes in its assembly before any gem is made. It provides nothing and takes no
settings, so it holds only scripts, components and the plain types they use. Magic's own scripts, the `.cs` assets
among the bundled resources, are built the same way by the project in that folder, into a library beside Magic, and
loaded before the project's code.

Both are loaded from their bytes, so a build can write them while the engine runs, and Core watches their folders:
once a build there has settled, it loads the code again beside the last build, whose types stay for whatever still
holds them, and the new assembly replaces the old. Every script is then made again, as a script is the class named
like its file, and every prefab after them, as a prefab names its components by their types and its scripts, so the
world spawns again what a domain placed and the scripts run as they are now
([hot reload](../Bundled/Resources/README.md#hot-reload)).

The folders of the engine's own assets and each gem's are known once the gems have loaded, and the assets are made
over them. A gem's own assets are its `Resources/` folder; the engine's built-in default assets, which come with
Magic whatever gems a project loads, are `src/Bundled/Resources/`. A release copies each beside its assembly. Any
other build copies nothing: it records the folder in the assembly, so assets are read where they are written, and
building stays fast.

## Gem settings

A gem with settings declares a class for them, an `ISettings`: plain data like a file type, which Core reads in its
JSON dialect ([Assets](#assets)), with a property per setting whose initialiser is its default and whose doc comment
is its description. The gem takes the class in its constructor as it takes any service.

`Settings` makes the object as the gem is made, and keeps it for the whole run. Its values come in layers, each
winning over the one before:

1. The class's initialisers.
2. The default preset, `Presets/Default.preset`: the project's own settings. The project's, under its `Assets/`,
   replaces the engine's, which sets nothing, so a project happy with the defaults has none.
3. The preset applied over it, what suits a kind of machine across every gem: `Presets/<Name>.preset` as
   `--preset Name` names it, then whichever is applied while running. Applying the default leaves it alone.
4. `--set Gem.path=value`, as many as given.
5. What is set while running, until another preset is applied.

A setting is named by its key, `Gem.path`: the gem as the project lists it, then where the setting sits in the gem's
settings, with dots between levels. On the command line and in a console, `Gem.path=value` gives its value too: JSON
when it parses as JSON, and a string when it does not.

A preset is an asset, read like any other: a JSON file of settings by key, with its `.meta` beside it, the project's
before the engine's; the samples' presets are the examples. The default and the applied one are read before any gem
is made, so even a setting a gem reads only as it starts takes them, and are applied again when their files change,
keeping what was set while running.

A game's options read and change settings by key, and apply presets by handle, as a settings window does. Each is
checked at once, and says why not, changing nothing, when the key names no gem with settings or the value does not
fit; applying a preset drops what was set while running. A change reaches the gems at the frame's sync point, after
`LateUpdate` and the wait for the render thread and before `Sync` ([The frame](#the-frame)): the new values are
copied into the object each gem holds, so a gem reads its settings on either engine thread and never sees a change
halfway, and reading a key back shows it at once. A gem that reads a setting every frame sees it change; one it
reads only as it starts, the GPU device opened say, is marked `[RequireRestart]`, and changing it ends the run and
starts it again at once with every setting as it is and the world as it was ([The run](#the-run)); a settings window
says so on the row. .NET's own data annotations say the rest: a range attribute bounds a number a window offers, and
a category attribute, on a property or its class, groups a setting with the other gems' settings of its kind in a
UI: a tab, then past the slash a header in it; a setting without a category sits under its gem's name.

`Settings` lists every setting as it is now, for a settings UI to show: its name, its description, which is its doc
comment (the build writes every assembly's comments into a documentation file beside it, so nothing repeats them),
the gem it belongs to, its type, its value and its attributes; and it lists the presets there are and which is
applied. `Settings` can be used from any thread.

A key for a gem the project does not list is left out of a preset, as one preset serves every project, and is an
error on the command line, where it is ignored; so is a key for a gem that takes no settings. A `--preset` no preset
has is an error, and the default alone is applied. Settings that do not fit a gem's class, an unknown key or a wrong
type, are an error naming the key, and that gem is not made.

## The log

`Debugging` is the log: how anything says what happened, from any thread, before the first gem and after the last.
`Verbose` is detail that is usually noise, logged only under `--verbose` and otherwise not even queued; `Info` is what
happened; `Warn` is something that works, only worse; `Error` is something that failed and is skipped or stood in for
while the frame goes on, counted for `--fail-on-error`; `Critical` is a run that cannot go on, so the process ends
there ([Crashes](#crashes)); and `Assert` is state that cannot be, `Critical` when its condition is false, on in every
build.

Each call makes a line: the time, the level, the message, the frame the main thread was in, the thread, and the file,
line and method it was called from, which the compiler fills in. The terminal shows all of that on one line, the
frame shown as none outside the frame loop. A failed assert's message is the condition's source, then the message if
there is one. The message is a plain string, built before the call whether the assert holds or not, so in the frame
loop it is a constant. Release builds name files from the repository, never the folders of the machine that built
them. A task let go that fails is an error from the line that let it go, so it counts for `--fail-on-error`; a
cancelled task has not failed.

### Sinks

The terminal gets every line. A gem sends them somewhere else as well (a file, an editor's console, a log service)
by implementing `ILogSink`, which is handed each line as it was logged.

- A sink is called on the log's background task, one line at a time in the order they were logged, never on an
  engine thread: a slow sink delays the log, never a frame. What it shares with the gem's hooks, which run on the
  engine's threads meanwhile, needs a lock.
- A sink gets the lines logged from when its gem is made until just before the gem is disposed, which waits a few
  seconds, until the sink has had every line logged before, so its `Dispose` can close what it writes to. The first
  sink of the run also gets the lines logged before any sink was added, the last several thousand of them, so a
  console has the whole run; the log keeps them only until then.
- A sink that throws is dropped, and what it threw is logged as an error.

### Crashes

A critical line, a failed assert and an exception nothing catches, on any thread, end the process at once:

1. Lines logged after it are dropped. Those the terminal has not printed yet go to a `.crash` file in `Logs/` beside
   Magic, named for the time, followed by the critical line and the stack it was called from.
2. The terminal gets the critical line and the crash file's path.
3. The process fails fast: no gem is disposed, the OS records the crash (a dump, and an attached debugger breaks),
   and the exit code is the OS's crash code.

The terminal and the crash file together hold the whole log. A sink never sees a critical line: a crash-reporting gem
sends the crash files it finds when the next run starts.

## Assets

Core reads the assets: the files under the project's `Assets/` and the engine's and each gem's `Resources/`, each
beside the `.meta` sidecar that gives it its id. It holds the asset types gems share, as plain data, and the
interfaces a gem plugs a file format into. How assets are laid out, found, read and reloaded, and the JSON dialect
every file is written in, is in [Resources](../Bundled/Resources/README.md).

Every asset type implements `IAsset<T>` of itself: its file extensions, a static, and its own handle, which `Assets`
gives it as it loads it, never read from the file. An asset holds what its users read, never how its file was read,
and every caller gets the same object while it is kept, so nobody changes one once it is loaded. A `Handle<T>` is an
asset by the id in its sidecar: what components, assets and the project hold, typed so a texture's cannot go where a
model's is wanted, with a none value.

`Assets` finds an asset by its path, a type's default and the assets in a folder, loads one, says what one was made
from and its version, and hands out one a gem made rather than read from a file, from any thread.

How a file becomes an asset depends on its type. A type whose file is the class as JSON implements `IJsonAsset<T>`,
and one whose file is UTF-8 text kept as its text implements `ITextAsset<T>`: `Assets` reads both, so the class is
all there is to write, and its sidecar holds only the id. Any other file format is an `IAssetLoader<T>` a gem provides
by implementing it, as any interface: it is handed the file's asset path, its bytes and the import settings its
sidecar holds beside the id, which it reads as a type of its own, so no loader handles the sidecar's format; it loads
asynchronously, and the assets it loads as it reads one, as a prefab loads the prefabs it places, are what that one
is made from. A type no gem reads is read as JSON or text when it is an `IJsonAsset<T>` or an `ITextAsset<T>`, and
refused otherwise. An `ILoadPostProcessor<T>`, which a gem provides the same way, works on an asset once its loader
has made it and before anyone gets it, on every load and reload (a model's clusters, a pipeline's passes): what it
returns is the asset everyone gets, it is handed the asset's version, a hash of its file and sidecar, to check what it
cached against, and what it loads as it works is what the asset is made from, so when one of those is made again, the
asset is made again after it.

Core's asset types are those more than one gem uses: one makes them, another reads them, as a `Prefab`, a bundle of
entities, is both a domain's cell and the project's player. An asset type is what the engine works with, not the
shape of its file: a `Prefab` is a tree of parts, each an entity's components and scripts as their own types and the
parts under it, which its loader made of the file, so nothing past the loader handles JSON. A type whose file is
already its shape, such as a `Domain`, is read as its class. A type only one gem reads is that gem's own, and
implements `IAsset<T>` all the same.

Every asset type has a default, `<Type>s/Default` with one of its extensions in the engine's own `Resources/`, which
`Assets` finds as the type's default; a project's or a gem's file at that path is an asset like any other, found by
its path, never the default. A type a renderer draws a failure as also has a failure asset for each kind of failure,
`<Type>s/Failures/<FailureType>`, found the same way.

**What is kept.** A loaded asset is kept for as long as anything that uses it holds it, and let go once nothing does;
asked for again, it loads again. Nothing counts references and nothing sets a budget: whoever uses an asset holds it,
and lets it go when it is done, so what is not in use is not in memory. Whoever keeps only what it made from an asset
still hears of changes to it, as one that was loaded is loaded again when its file changes. An asset a gem made, such
as a card's atlas, is found by its handle the same way, for as long as it is held, under a handle the gem derives from
what it made it from, so making it again replaces it.

What happens to assets is told on [Events](#events), each message of the asset's type, so a gem watches only the
types it uses: that an asset finished loading, with the asset; that a loaded asset's file or sidecar changed, or an
asset it was made from was made again, and it was made again, with the new object under the same handle, which
whoever holds the old one takes in its place; and that files came, went, moved or were written, so what a lookup by
path or folder answers may have changed, told once what changed has loaded again.

**JSON.** There is one dialect, which `Json` defines, and everything that is JSON is read and written in it; the
rules an author follows are in [Resources](../Bundled/Resources/README.md#reading-an-asset). Any type reads in it as
it is, Core's, a gem's or a game's: .NET's serializer finds its members by reflection, so nothing is declared for it.
`Json` reads a value of a known type from bytes, skipping a byte order mark, and says why when it cannot; it reads and
writes a value whose type is known only at run time, as a saved world holds a component by its type's name, and says
why when JSON cannot hold it; and its options serve the serializer for anything else. Magic is never trimmed or
compiled ahead of time, as gems load from disk at run time, so the reflection is always there.

## The cache

What is slow to make is kept so that a later run reads it rather than making it again: a model's clusters, a
compiled shader. It works as the assets do: Core holds the entries, and each type of thing kept has a gem that says
how it is written. `Caches` holds the entries, beside Magic in `Cache/` as the log is in `Logs/`, and reads and
writes them from any thread, each read and write given the key and the version below. How a `T` is written and read
back, and its version, is an `ICache<T>`, provided by the gem that makes `T` as it provides any interface.

- **The key** is the id of what an entry was made from: an asset's id, so any asset can have one entry of each type,
  or a hash naming what is not an asset, such as a shader's source and stage, so each variant of a shader has its own.
- **The version** is a hash of everything the entry was made from: the asset's version, which its post-processor is
  handed, and the settings it was made with. Read with another version, or written by another version of the type's
  `ICache<T>`, an entry is dirty: the read fails, and the caller makes it again and writes it over it. So an asset
  that changes, while the engine runs or while it does not, has what was made from it made again.
- An entry is a file under `Cache/`, in a folder per type: the version it was made from and the type's version, then
  what the type's `ICache<T>` wrote. A write goes beside the entry first and is then moved over it, so a reader, or
  the next run after a crash, never finds half an entry; one that fails is a warning, and it is made again next time.
- Whoever reads an entry keeps what it needs of it: the cache holds nothing in memory. Deleting `Cache/` is always
  safe: whatever was in it is made again as it is needed. The entries of an asset whose file is gone stay until the
  folder is deleted.

So a gem that compiles a shader reads the cache first, and only on a miss compiles and writes what it made.

## Components

A component is an unmanaged struct that implements `IComponent`, a marker with nothing in it: the world takes only
those, and a file names one by its type's name, which Core finds in the code loaded, its own, the project's and each
gem's (`Assemblies`), so declaring one is all a gem or a game does. Two types of one name are an error, as a file
could mean either. A game's component reloads as its scripts do, since only data names it: built again, it is read
again and the entities holding it respawn. The engine's own are its contract, as its interfaces are, since the world,
the renderer and every script are compiled against them; they change with a restart. The samples' components are
the examples.

Where an entity is, its `Transform`'s position, is floats in metres, as every position and size in the engine is;
its matrix is what takes its model into the world.

A tag is a label with no data that a file names without any code declaring it: a `tags` list on an entity, beside its
components, gives it a `Tags` component holding up to eight tags, each a stable hash of its name. The engine's own
are the spawn tag, where a domain puts the player, the player tag, the player's camera, which detail is kept
nearest to ([The world](#the-world)), and the sun tag, the directional light that is the world's sun. An entity's scripts are held the same way: a `Scripts` component of up to eight
`.cs` asset handles, which a prefab's scripts list gives it and any system can query ([Scripts](#scripts)).

## Scripts

A script (`IScript`) is what an entity does, as its components are what it holds: a `.cs` asset whose class, named
like the file, implements it. The project's code ([The project file](#the-project-file)), the gem it belongs to, or
Core for the engine's own, compiles it, and the gem that runs scripts reads the asset (`Script`) as that class, the
type named like the file in the code loaded (`Assemblies`), as Core reads a component's struct. Scripts work as
components do: a prefab's part holds its scripts with their values, and each entity spawned from the part gets its
own copy of each, made for it.

A script's constructor takes the entity it is attached to first, then the services it needs, and only keeps them: it
does no work, as its values are not set yet. Its public settable properties are its values, each initialiser the
default a prefab's value replaces; `Start()` is where it begins, on the main thread, with its values set and the
entity's components there to read. An `IBehavior` runs each frame: `Update`, `FixedUpdate` and `LateUpdate`, each
optional, called on the main thread, `FixedUpdate` every step and the others each frame, handed the time since the
last call as the frame's delta. A system script is a script that is also a system, of any kind the world takes:
added to the ECS while its entity lives; the entity it is made for holds it and is no entity it runs over. An
`IPipelineScript` records part of a frame: a pass of kind `script` names it, and the renderer makes it for each
listing of the frame it builds and calls it on the render thread to draw into the pass's target, with pipelines of
its own, never on an entity; it is the hook at a point of the frame for code that is no gem, a HUD or an overlay. A
class marked `[Impatient]` starts even while a domain loads in place of the world; any other waits until the domain
is there, so it never sees a world half loaded. The loading domain's own scripts, such as a progress bar, are
impatient, or never run. The samples' scripts are the examples of each kind.

The `Scripts` component says which scripts an entity has, by their `.cs` assets: the gem that runs scripts makes a
copy of each named and has no copy of, and ends a copy whose handle is gone or whose entity is destroyed, disposing
it when it is `IDisposable`. Adding a handle attaches a script with its defaults; removing one detaches it. The world
gives an entity its own copy of a script with a prefab's values through `IScripts`: its handle joins the entity's
`Scripts`, and in the next frame, once whatever spawned with it is all there, the copy is made for the entity, given
the values and started, on the main thread. `IScripts` also makes a script of a class that runs on no entity, as a
pipeline script does, with the services its constructor asks for, from any thread.

## The world

`World` is the game world, and the one place game code and gems act on it. Core always makes it, and opens the
project's entry point through it on the first frame. Underneath, the pieces stay separate: the ECS gem keeps the
entities and runs the systems (`IEcs`), the scripting gem runs the scripts (`IScripts`), and the assets load what the
world opens and spawns. Only those pieces take `IEcs`; everything else takes `World`. Without a gem that provides the
ECS, the world holds no entities: no domain opens, which is warned about, a call that needs entities throws saying
so, and ending the game still works.

`World` holds the entities, their components, queries and systems as the ECS gem keeps them; puts an asset in the
world at a transform as the asset says it becomes entities, loading it first when asked to, handing back the
entities made, the caller's to destroy, a `Prefab` as its parts, each placed under its parent, a `Model` as one
entity that draws it; gives an entity its own copy of a script ([Scripts](#scripts)); opens and closes domains and
says which are open and whether one is loading; saves itself as data and puts itself back from it; and ends the
game, which stops the frames and ends the gems. It is the main thread's, but opening a domain, spawning an asset
that must load first and saving, which any thread may call. It does nothing each frame: opening a domain reads on
workers and spawns on the main thread, and what changes on disk is told on [Events](#events). An asset type that can
be put in the world implements `ISpawnable`, saying how it becomes entities, as an `IJsonAsset` says how it is read.

A domain is a folder holding a `.domain` file beside the prefabs it spawns: every one in the folder, cells named for
their corners and any other, such as the globals, at the domain's origin
([Domains](../Bundled/Resources/Domains/README.md)). Opening one reads them all on workers, with the models they
draw, hands them to every gem that needs time to take in what spawns before it can draw or simulate it
(`IPreloader`: a renderer uploading models and compiling pipelines, a physics gem fitting shapes), and spawns them
all in one frame once every such gem is done, so the domain is there whole in its first frame: nothing dissolves in,
stands still or starts late. Nothing is streamed: whatever draws the world picks each thing's detail by its distance
from the player's camera, so the whole world is there at once.

- The project's entry point opens at start-up, or the default domain when it does not.
- Opening a domain in place of the open ones closes them and shows the project's loading domain until the new one
  is there, which is once every gem has taken it in. It appears in the frame the loading domain closes, with the
  project's player put at its spawn: the first entity tagged as the spawn in its prefabs that are not cells, else in
  its cells. The world says it is loading from when the open domains close until then. The loading domain itself is
  not taken in first, so it shows as soon as it can.
- A gem is a preloader by implementing `IPreloader`. It is handed the prefabs about to spawn, the player among them,
  and the task it returns ends once everything they name is taken in, or cannot be, and hands back what keeps that
  taken in, which the domain holds while it is open. The reads are the first half of an open's progress, the gems'
  taking in the second.
- Opening one beside them shows no loading domain and puts no player.
- Closing a domain destroys what it spawned.
- While a domain is open, a prefab of it that is read again, as its file or a prefab it places changed, is spawned
  again where it is; prefabs added to its folder spawn, and those removed go. The player is not spawned again: what
  the player has done is the game's.

Saving writes the whole world as data, a `WorldState`: the domains open, with the entities each of their prefabs
spawned, and every entity with its components as JSON by their types' names and its scripts by their assets with their
values as JSON. It takes the world in one frame on the main thread, each entity's components copied out and its
scripts' values read, so the save is of one moment, then writes the JSON on a worker, reporting its progress. It holds
nothing of the code that made it, so it outlives the run, and restores into whatever code is loaded then, as a file
read would. Restoring puts the world back from one on the main thread, in the frame it is called: it closes what is
open, makes every entity anew, gives each its components and puts its scripts back without starting them, their
classes found in the code loaded now, and opens each domain again around the entities its prefabs had spawned, so
closing one and a prefab read again still find them. Each domain's folder is then read on workers as opening one
does, so a prefab added since spawns and one gone has its entities destroyed, and the models its prefabs draw are
held; until then they come in as anything spawned at run time does. An `Entity` is saved as its id and read back as
the entity made for it, so what refers to another entity, in a component or a script, still does; the id of one
destroyed before the save reads as none. The engine saves the world as a run ends to restart and restores it before
the first frame of the next ([The run](#the-run)). A component no code loaded declares, a script whose class is not
found, or one whose saved values no longer fit it, is an error, and the entity goes without it.

## The GPU

Core holds what every gem that draws shares, and no graphics API. `IGpu` is the GPU one level above the API: make
buffers, textures, samplers and pipelines, upload to them, and record the frame's commands into it, all on the render
thread. The provider submits and presents them in its `Render` hook; it is made before every gem that takes it, so
its `Render` runs after theirs, and a gem that takes it records in `Render`. A render target is a `GpuTexture`: a
texture a gem made (a render texture, which it can then sample, as an editor's viewport is), or a window's, which
`IGpu` hands out for a window. Whatever draws asks `IGpu` the format and size of its target and treats the two
alike; a window's can be drawn into, never sampled or read, but `IGpu` can capture what it shows next, for a
screenshot. The frame's commands are plain data, each command with the handles and bytes it points into. Drawing
and computing happen inside a pass: beginning a render or compute pass hands back an object holding only the
commands legal inside it, which chain, and ends the pass when disposed, so a pass is a `using` block, as a debug
group is. Copies and mipmaps go between passes.

`IShaderCompiler` turns HLSL into the bytecode the GPU takes, in the format `IGpu` asks for, with the counts of what
it binds, compiled on a worker. Each `#include "path"` is the shader asset at `Shaders/path`, which the compiler
loads. What it compiled is kept in [the cache](#the-cache), so a shader compiles once. Shaders are written against the
shader library in [Resources/Shaders](../Bundled/Resources/Shaders/README.md).

What a renderer draws from the world is components: a `Camera` for each view, what each entity draws, the lights,
the sun among them by its tag, the sky, the `Weather`, which says the wind and the clouds as they are now, and
`PostProcessing`, which names the world's effects as a `Pipeline`. A `Pipeline` is an ordered list of
`Pass`es and other pipelines, and a `Pass` one step of a frame: the stage it runs in, its shader, and the resources it
reads, writes and makes, by name. Both are plain JSON assets, so an effect is data a world names, and what a renderer
makes of them is in its README; their keys are the two types' properties, documented on them, and the bundled
pipelines and passes are the examples. A pipeline loads as any asset does, and comes with the passes it runs, nested
pipelines in their place and params merged, made by a gem that post-processes pipelines, so a renderer never walks a
pipeline itself. Which pipeline a renderer's frame runs is one of its settings, so the project or the machine picks
it. A debug view to show in place of the finished image is chosen by name on [Events](#events), from whatever
chooses it, a debug window or a console command, to whatever draws the frame, which decides what the name shows.

## Physics

What is solid is components, as what is drawn is: an entity with a `Transform` and a `Physics` is a body, and a
physics gem simulates every one, once per fixed step on a thread of its own, moving the transform as the body moves
and writing the body's velocities back to the component. A script moves a body by writing its transform and throws it
by writing its velocity; there is no handle to hold. `Physics` says what moves the body, its shape, its surface, its
bounce and its mass; the shape's size is the entity's model's at the transform's scale, so no size is written twice.

An entity that opts in with a `Contacts` component beside its `Physics` is told what the body touched: after every
step, each body in contact with it as an entry, up to a few, with the other entity, the point and the normal in the
world, the step's impulse and the phase, entering the step they begin to touch, staying while they go on touching,
and exiting for the one step after they part. A script reads it in `Update`. Collisions reach the game this way, as
data on the entity, never as messages.

`IPhysics` answers questions about the solid world, from the bodies as the last step left them: a cast, the nearest
body a segment hits, with the entity, the point, the normal and the distance, or none; an overlap, the bodies within
a radius of a point, written into a span the caller owns. Each takes a filter of which kinds of body to consider,
static, dynamic or kinematic, all by default. It is the main thread's, in `Update`, `FixedUpdate` or `LateUpdate`,
waiting for a step still running. Core turns a pixel into the world point under it at a depth in front of a camera,
which with the camera's position is the pointer's ray: casting along it is what the pointer is over.

## Audio

What is heard is components too: an entity with a `Sound` plays the `Audio` it names, a sound decoded whole by the
gem that reads audio files. A sound in the world is heard from the player's camera, from the side it is on and
quieter the farther it is, out to the range it says, past which it costs nothing; what is solid, the bodies of
[Physics](#physics), muffles it when it stands between the two and echoes it off what is around them. A sound
everywhere is heard alike wherever the player is, as music and ambience are. `Sound` holds every value a sound plays
with, and a script plays, pauses, stops, seeks or changes a sound by writing it, as it moves a body by writing a
transform; the gem that plays sounds writes back how far in each is, and when one that does not loop has ended.

An entity with a `Physics` sounds like what it is made of when an `Acoustics` beside it says, a material from hard and
ringing to soft and dead, and generic otherwise: how much of a sound it soaks up, scatters and lets through. Nothing
else about the world is said twice: what muffles and echoes is what collides and stays put or is moved by code, each as
its model's surface at its transform; what falls and is pushed around is too small and too restless to be worth it.

## The debug UI

What a gem or script shows the developer while the game runs is `IDebugUI`, which a UI gem provides and draws over
the window as it likes. It does two things. **Reports**, from any thread, shown with the next frame drawn: text puts a
few words at a place in the world, or a line on screen above the others, for the frame it is called in, so it is
called every frame they should show, and a line on screen this way is never logged; an info, warning or error report
puts a line on screen for a while, or, for a warning or error given a position, text at that place, and is logged as
the `Debugging` call of its level is, a warning or error once while it stays up. Text in the world, warnings and
errors take a tooltip, shown while the mouse rests on them. Check whether the UI is enabled before building text:
it is not while nothing shows. **Widgets** are windows drawn in immediate mode on the main thread, by a gem made
after the UI gem, in its `Update`: what is called in a frame is that frame's windows. A begin opens a window, or a
view nested in the one open, and an end closes it; between them go labels, buttons, fields, sliders, choices, tabs,
headers, tables of rows and a right-click menu, each answering from the frame's input.

`Debugging` only logs, so its warning never shows on screen; `IDebugUI`'s warning is the one that does, and is in the
log all the same.

## Telemetry

What the engine measures is published as .NET metrics (`System.Diagnostics.Metrics`), so Core has no telemetry types
of its own:

- **Tracking.** Each part makes its own `Meter`, `Magic.Core` for Core and `Magic.Gems.<Gem>` for a gem, and records
  on its instruments (counters, histograms, gauges) with tags. Recording costs next to nothing while nothing listens,
  so it belongs in the frame loop.
- **Naming.** Instruments and tags are named as OpenTelemetry names them, so exporters, dashboards and the debug UI
  lay them out alike: lowercase and dotted, from the general to the particular, starting with the meter's name in
  snake_case (`magic.core.` for Core, `magic.gems.<gem>.` for a gem); a histogram of times ends in `.duration`. Units
  are `s` for seconds, `By` for bytes and a count's in braces (`{entity}`). A tag is namespaced the same way, and has
  few values, such as a gem or a phase, never an id or a path: every set of values is a series of its own in every
  tool. A histogram of times advises bucket boundaries that fit what it times, as exporters' own are made for
  milliseconds.
- **Doing something with it.** Any number of gems listen, with a `MeterListener` or OpenTelemetry's exporters, and
  send what they hear to a backend, a database, a profiler view or the log every so often. `dotnet-counters` shows
  Core's meter with no gem at all.

Core times the frame, the main thread's wait for the render thread, each gem's hook by gem and phase, each piece of
work handed to an engine thread, and each asset load by type; the hooks, the waits and the handed work are timed only
while something listens. The frame timings' buckets have edges at the common frame budgets, and a load's span the
range a load takes. The instrument and tag names are constants on one Core type, so a listener that singles one out
compares against the name Core records under, and a rename is a build error rather than a line that quietly stops
appearing.

A listener's callback runs inside the code that recorded, on its thread: the main or render thread for Core's
instruments. It adds the numbers up and hands them to a thread of its own, never blocking. What it throws surfaces
where the measurement was recorded: a gem recording in a hook leaves the frame loop, and Core's own instruments crash
the run. A listener picks Magic's instruments by their meter's name as they are published, and enables measurement
events on those.
