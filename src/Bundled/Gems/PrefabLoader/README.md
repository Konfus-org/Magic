# PrefabLoader

Reads `.prefab` files into `Prefab`s: provides `IAssetLoader<Prefab>`
([Core's README](../../../Core/README.md#assets)), so whatever asks the assets for a prefab gets one ready to spawn.
The file format is in [Prefabs](../../Resources/Prefabs/README.md). It takes the assets, to load what a file places,
gives and attaches. It has no settings.

## Reading

A file becomes a tree of parts, one for each entity it holds:

- **Components.** A file names each component by its type's name, found in the code loaded through Core's
  `Assemblies`, its JSON read as that type in Core's dialect. A name no code loaded declares, or one two types share,
  or JSON that is not the type's fields, is an error, logged once per name, and the component is left out.
- **Tags.** An entity's tags, up to the few the component holds, become a `Tags` among its components; past that, or
  an empty name, is warned about and left out.
- **Scripts.** Each script entry names the `.cs` asset that declares it by its id, and every other key is a value for
  its public property of that name, as Core's JSON dialect names it (camelCase), read as the property's type. A part
  holds its scripts with their values, and a `Scripts` component naming them among its components; how a script is
  then made for each entity spawned from the part, and given those values, is the
  [scripting gem's](../Scripting/README.md). A script that does not load, declares no script, or is given a key it has
  no property for is an error, and the part goes without it; one named twice, or past what an entity holds, is
  warned about and left out.
- **Children.** An entity's children are the parts under it. One that places a prefab has that prefab loaded, and
  its parts go under it, before its own children: shared, not copied, as nothing changes a prefab once it is made.

What a file places and attaches is loaded as it is read, so it is what the prefab is made from, as the assets track
it: when a placed prefab changes, every prefab placing it is read again, and whatever spawned it hears so on `Events`.
A prefab that places itself, directly or through others, fails rather than waits for ever. A script not named by its
id, or a placed prefab that does not load, fails the file. Its `.meta` holds only its id.
