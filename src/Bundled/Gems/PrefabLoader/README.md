# PrefabLoader

Reads `.prefab` files into `Prefab`s: provides `IAssetLoader<Prefab>`
([Core's README](../../../Core/README.md#assets)), so whatever asks the assets for a prefab gets one ready to spawn.
The file format is in [Prefabs](../../Resources/Prefabs/README.md). It takes the assets, to load what a file places and
attaches, and `IScripts` when a gem provides it, to make scripts. It has no settings.

## Reading

A file becomes a tree of parts (`PrefabPart`), one for each entity it holds:

- **Components.** A file names each component by its type's name: any struct implementing `IComponent` that Core, the
  project's code or a loaded gem declares (`Code.Component`), its JSON read as that type in Core's dialect. A name that
  is no such type is an error, logged once, and that component is left out.
- **Tags.** An entity's `tags`, up to eight, become a `Tags` among its components; past eight, or an empty name, is
  warned about and left out.
- **Scripts.** Each script entry names a script by its id; the script is loaded, made by whatever provides `IScripts`
  (`Make`), and every other key set on its public property of that name, read as the property's type. A part holds its
  scripts made, as it holds its components, and each entity spawned from it runs a copy. A script that does not load or
  cannot be made, a key it has no property for, or no gem that makes scripts, is an error, and the part goes without it.
- **Children.** An entity's `children` are the parts under it. One that names a `prefab` has that prefab loaded, and
  its parts go under it, before its own children: shared, not copied, as nothing changes a prefab once it is made.

What a file places and attaches is loaded as it is read, so it is what the prefab is made from
(`Assets.Dependencies`): when a placed prefab changes, every prefab placing it is read again, and whatever spawned
it hears so (`AssetReloaded<Prefab>`). A prefab that places itself, directly or through others, fails rather than waits
for ever. A component that is not one, a script not named by its id, or a placed prefab that does not load, fails the
file. Its `.meta` holds only its id.
