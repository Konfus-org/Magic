# Templates

Two `dotnet new` project templates. Visual Studio lists them in **File > New > Project** (search
"Magic"); the `dotnet` CLI uses the short names. Install both, or reinstall after editing one, with

```
pwsh Tools/install-templates.ps1
```

## Magic Project (`magicproject`)

A game project: `<Name>.magic`, a starting domain under `Assets\Domains\<Name>\` (a camera, a sun and a cube),
and `Scripts\` compiled by `<Name>.csproj` into the project's own gem. The engine finds every gem dll under
the project folder, so nothing is registered anywhere.

```
dotnet new magicproject -n MyGame -o C:\Games\MyGame --EnginePath C:\Users\me\Projects\Magic
dotnet build C:\Games\MyGame
Build\net10.0\Debug\bin\Magic.exe --project C:\Games\MyGame
```

`--EnginePath` is the engine repository; the `MAGIC_ROOT` environment variable overrides it at build time,
so a project can leave it empty. Gems compile against the engine's built `Magic.dll` of the same
configuration, so build the engine first (`dotnet build Core\Magic.csproj`).

A project builds like the engine does: `Build\net10.0\<Configuration>\bin\` holds every gem dll and
`obj\<Gem>\` the intermediates, set by the project's `Directory.Build.props`. That file is the whole
build convention; a project can hold as many gems as it likes, each a csproj under `Gems\`.

## Magic Gem (`magicgem`)

A gem on its own. The same template works in two places, told apart by whether a Magic project's
`Directory.Build.props` sits above it:

- **In a project**, under `Gems\`: it builds into the project's `Build\` tree and loads with the project.

  ```
  dotnet new magicgem -n Physics -o C:\Games\MyGame\Gems\Physics --Author "Konfus" --Description "Rigid bodies."
  ```

- **In the engine repo**, a default gem: `Gems\` is the conventional home, but any folder in the repo works.
  The build recognises a gem by `<IsMagicGem>true</IsMagicGem>` set in its csproj before the SDK import, the
  Core reference is anchored to the repo root, and the dll lands in `bin\Gems\` next to the exe. Add it to
  `Core\Magic.csproj`'s `DefaultGem` list to ship it with the engine.

  ```
  dotnet new magicgem -n Physics -o Gems\Physics --Author "Konfus" --Description "Rigid bodies and collision."
  dotnet sln Magic.slnx add Gems\Physics\Physics.csproj --solution-folder Gems
  ```

## Writing a gem

A gem is the one class in its assembly that implements `IGem`. Its constructor parameters are its dependencies,
the Core interfaces it implements are the services it offers (`IAssetLoader<T>`, `IOverlay`, `IRendering`, ...),
and `Dispose` runs on unload. The frame loop calls its hooks once a frame, gems in load order: `Update`,
`FixedUpdate`, `LateUpdate` and `Render`, each with the frame's `Frame` (delta in seconds, the events since the
last frame). Implement `Reloading`/`Reloaded` to carry state across a hot reload.

The gem's name is its assembly name. Two csproj properties are the only other things the host reads, stamped into
the assembly by `Directory.Build.props`: `<GemStatic>true</GemStatic>` marks a gem that loads once and is never
hot reloaded (a rebuild while the engine runs is ignored until restart), for the core systems the host itself
holds on to: windowing, the ECS, logging. The gems a static gem depends on cannot be reloaded under it either.
`<GemDependsOn>SDL</GemDependsOn>` names gems (`;` separated) that must load first without a service between them
(say, one that owns a library's init and quit). It loads after them and unloads before them.

Host services a gem can take: `Project` (paths), `IFileSystem` (disk), `Assets` (load an asset by handle; it keeps
nothing) and `Events` (`Publish` an `Event`, or `Watch` a type). Every handle a gem takes from these (an event
watch, an ECS query) it disposes in its own `Dispose`; the host tracks none of them, and one left behind keeps the
old assembly alive after a hot reload (logged).

Uninstall with `dotnet new uninstall Tools\VSTemplates\MagicGem` and `dotnet new uninstall Tools\VSTemplates\MagicProject`.
