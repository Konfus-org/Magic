# Templates

Three `dotnet new` templates: two projects, which Visual Studio lists in **File > New > Project** (search
"Magic"), and an item; the `dotnet` CLI uses the short names. Install them, or reinstall after editing one, with

```
pwsh Tools/install-templates.ps1
```

## Magic Project (`magicproject`)

A game project: `<Name>.magic`, a starting domain under `Assets\Domains\<Name>\` (a camera, a sun and a cube),
and `Scripts\` compiled by `<Name>.csproj` into the project's own gem. The engine finds every gem dll under
the project folder, so nothing is registered anywhere.

```powershell
dotnet new magicproject -n MyGame -o ..\MyGame --EnginePath $PWD
dotnet build ..\MyGame
Build\net10.0\Debug\bin\Magic.exe --project ..\MyGame
```

`--EnginePath` is the engine repository; the `MAGIC_ROOT` environment variable overrides it at build time,
so a project can leave it empty. Gems compile against the engine's built `Magic.dll` of the same configuration,
which holds every type a gem builds on, so build the engine first (`dotnet build Magic\Core\Core.csproj`).

A project builds like the engine does: `Build\net10.0\<Configuration>\bin\` holds every gem dll and
`obj\<Gem>\` the intermediates, set by the project's `Directory.Build.props`. That file is the whole
build convention; a project can hold as many gems as it likes, each a csproj under `Gems\`.

## Magic Gem (`magicgem`)

A gem on its own. The same template works in two places, told apart by whether a Magic project's
`Directory.Build.props` sits above it:

- **In a project**, under `Gems\`: it builds into the project's `Build\` tree and loads with the project.

  ```
  dotnet new magicgem -n Physics -o ..\MyGame\Gems\Physics --Author "Konfus" --Description "Rigid bodies."
  ```

- **In the engine repo**, a default gem: `Magic\Gems\` is the home (in the area it belongs to: `Assets`, `Debug`,
  `LODs`, `Rendering`, `World`), but any folder in the repo works.
  The build recognises a gem by `<IsMagicGem>true</IsMagicGem>` set in its csproj before the SDK import, the
  Core reference is anchored to the repo root, and the dll lands in `bin\Gems\` next to the exe. Add it to the
  solution, which builds every gem, and to `Magic\Core\Core.csproj`'s `DefaultGem` list to ship it with the engine
  (building Core alone builds that list, in order: a gem after any gem it references).

  ```
  dotnet new magicgem -n Physics -o Magic\Gems\Physics --Author "Konfus" --Description "Rigid bodies and collision."
  dotnet sln Magic-Wizard.slnx add Magic\Gems\Physics\Physics.csproj --solution-folder Magic\Gems\Physics
  ```

## Magic Sample (`magicsample`)

A sample for the engine's own `Samples` project: a folder under `Samples\Assets\` holding a domain
(a camera, a light, a floor and a turning cube) and the script that turns it. Every id it needs is drawn at random,
so it never collides with another sample's. The Samples project compiles the script with its next build, and the
console's `portal` lists the domain, so a sample is in the list by being there.

```
dotnet new magicsample -n Fountain -o Samples\Assets
dotnet build Samples
Build\net10.0\Debug\bin\Magic.exe --project Samples
> portal Fountain
```

In Visual Studio it is **Add > New Item** on the Samples project, with `Assets` as the location. The template leans
on what the Samples project has (the shared `CameraController` and `OrbitSystem` scripts), so it
is of no use in a game project.

## Writing a gem

A gem is the one class in its assembly that implements `IGem`, at the root of the gem's folder. Beside it go
`Contexts` (its public data: components, assets, events, and settings another gem reads), `Services` (its public interfaces and the classes
that do the work), `Internal` (everything else), `Resources` (its own assets, found while the gem is loaded) and
`Tests` (its unit tests, [TESTING.md](../../TESTING.md)). The csproj is named for the folder and keeps the template's
shape (`MagicGem\MyMagicGem.csproj`), as every engine gem's does.

Its constructor parameters are its dependencies (a `T?` is optional: null when no gem provides it; a `T[]` is every
provider there is, in load order, possibly none), the interfaces it implements are the services it offers
(`IAssetLoader<T>`, `ILogger`, `IRendering`, ...), and `Dispose` runs on unload. The frame loop calls its hooks once
a frame, gems in load order: `Update`, `FixedUpdate`, `LateUpdate` and `Render`, each with the frame's `Frame`
(delta in seconds, the events since the last frame), then `Present` on the render thread in reverse order.
Implement `Reloading`/`Reloaded` to carry state across a hot reload.

The gem's name is its assembly name. `<GemStatic>true</GemStatic>` in its csproj marks a gem that loads once and is
never hot reloaded (a rebuild while the engine runs is ignored until restart). Every engine gem is static; hot reload
is for what changes while a game is made, a project's own gems and scripts, so the templates leave it off. A gem references Core only: every type more than one
gem names is Core's, so any gem can be swapped without touching another. The exception is a gem that reads another
gem's public markers, as DeferredRenderer and ChunkStreaming read EntityHierarchy's `Static`, `Hidden` and
`Settled`: it references that gem's csproj with `Private="false" ExcludeAssets="runtime"`. The gem referenced loads
first, whether or not the project's `gems` list names it, unloads after, and reloads the other when it reloads.

What a gem can take, all of it Core's: `Project` (paths and its `.magic` sections), `IFileSystem` (disk), `Events`
(`Publish` any struct, `Watch` a type), `Threads` and `MainThread`, `Settings` and its own `[Settings]` classes,
`ITelemetry` (numbers the debug display shows), `IServices` (the lookup by type, for the few gems that construct what
they learn of at run time), `Randomness`, `Assets`, `World`, `IDebugUI` (text in the world, notes on screen) and
`IConsoleCommands`; and what other gems provide behind Core's interfaces: `IEcs` (`Add` puts an `ISystem` on a
phase: `Update`, `FixedUpdate`, `LateUpdate`, `Render`, or `Overlay` for what draws on top of the scene),
`IRendering`, `IInput`, `IWindowRegistry` and the rest. Every handle a gem takes from these (an event watch, an ECS
query, a system, a console command) it disposes in its own `Dispose`; the host tracks none of them, and one left
behind keeps the old assembly alive after a hot reload (logged).

A script language is a gem that loads `Script` assets (an `IAssetLoader<Script>`): it says how one is made for an
entity, and whether it is impatient or always updated, and the Scripting gem runs it. CSharpScripting is the C# one.

Uninstall with `dotnet new uninstall Tools\VSTemplates\MagicGem`, `dotnet new uninstall Tools\VSTemplates\MagicProject`
and `dotnet new uninstall Tools\VSTemplates\MagicSample`.
