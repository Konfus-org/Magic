# Templates

## Magic Gem (`magicgem`)

A `dotnet new` project template for a gem. Visual Studio picks these up in
**File > New > Project** (search "Magic Gem"); the `dotnet` CLI uses the short name.

Install once from the repo root (re-run after editing the template):

```
dotnet new install .\Templates\MagicGem
```

Create a gem. `Gems\` is the conventional home, but any folder in the repo works: the
build recognises a gem by the `<Name>.meta` file next to its csproj, and the Core
reference is anchored to the repo root.

```
dotnet new magicgem -n Physics -o Gems\Physics --Author "Konfus" --Description "Rigid bodies and collision."
dotnet sln Magic.slnx add Gems\Physics\Physics.csproj --solution-folder Gems
```

In Visual Studio, set the location to the `Gems` folder and add the new project
to the `Gems` solution folder.

Uninstall with `dotnet new uninstall .\Templates\MagicGem`.
