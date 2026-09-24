# Templates

## Magic Gem (`magicgem`)

A `dotnet new` project template for a gem. Visual Studio picks these up in
**File > New > Project** (search "Magic Gem"); the `dotnet` CLI uses the short name.

Install once from the repo root (re-run after editing the template):

```
dotnet new install .\Templates\MagicGem
```

Create a gem. `Gems\` is the conventional home, but any folder in the repo works: the
build recognises a gem by `<IsMagicGem>true</IsMagicGem>` set in its csproj before the
SDK import (see the template csproj), and the Core reference is anchored to the repo root.

```
dotnet new magicgem -n Physics -o Gems\Physics --Author "Konfus" --Description "Rigid bodies and collision."
dotnet sln Magic.slnx add Gems\Physics\Physics.csproj --solution-folder Gems
```

In Visual Studio, set the location to the `Gems` folder and add the new project
to the `Gems` solution folder.

A gem is a class marked `[Gem(name, version, description)]`. Its constructor parameters are
its dependencies, `[GemExport(typeof(IContract))]` classes are the services it offers, and `IDisposable` runs on
unload. There is no metadata file.

One assembly holds one `[Gem]` class. `Static = true` on `[Gem]` marks a gem that loads once
and is never hot reloaded (a rebuild while the engine runs is ignored until restart), for the
core systems the host itself holds on to: windowing, the ECS, logging. The gems a static gem
depends on cannot be reloaded under it either. Implement `IHotReloadable` on the gem class to
carry state across a hot reload.

A gem that needs another gem loaded first without going through a service (say, one that owns a
library's init and quit) names it: `[Gem(..., DependsOn = ["SDL"])]`. It loads after that gem and
unloads before it.

Host services a gem can take: `Project` (paths), `IFileSystem` (disk), `SystemScheduler` (per-frame systems),
`AssetManager` (load an asset by handle; it keeps nothing) and `EventBus` (`Subscribe<T>` / `Publish<T>` on
struct events such as `AssetModified`). Every handle a gem takes from these it disposes in its own `Dispose`;
the host tracks none of them, and one left behind keeps the old assembly alive after a hot reload (logged).

Uninstall with `dotnet new uninstall .\Templates\MagicGem`.
