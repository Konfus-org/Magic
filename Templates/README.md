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
its dependencies, `[GemExport]` classes are the services it offers, and `IDisposable` runs on
unload. There is no metadata file.

Uninstall with `dotnet new uninstall .\Templates\MagicGem`.
