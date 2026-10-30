# Prefabs

A prefab is a group of entities spawned together: a `.prefab` file. Positions and rotations are relative to where it
is spawned. The prefab loader's file type documents the format, and the files here are examples.

Each entity in the file can have a name, up to eight tags, components by type name, up to eight scripts, children,
and a prefab to place under it.

- Each component is written as that type's JSON ([Assets](../README.md#reading-an-asset)).
- Each script is a `.cs` asset by id. The entry's other keys set the script's public properties
  ([Scripts](../../../Core/README.md#scripts)).
- The `spawn` tag marks a domain's spawn point, and `player` the player's camera.
- A prefab cannot place itself.

A domain spawns every prefab in its folder ([Domains](../Domains/README.md)). Game code spawns one through the
`World` service, and the `.magic` file can name one as the player.
