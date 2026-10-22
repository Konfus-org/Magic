# Domains

A domain is a piece of the world that opens as one: a folder with a `<Name>.domain` file and the prefabs it spawns.
The `.domain` file holds the domain's settings, documented on the `Domain` type. The domains here are examples.

Every prefab in the folder spawns when the domain opens:

- **Cells**, named `x_y_z.prefab` (such as `-1_0_3`), spawn at their coordinates times the domain's cell size.
- **Any other prefab**, such as `globals.prefab`, spawns at the domain's origin. Put the sky, the sun and the weather
  here, and an entity tagged `spawn`, where the project's player is put.

A project names its first domain, and can name its own loading domain, in its `.magic` file
([the project file](../../../Core/README.md#the-project-file)). Game code opens others through the `World` service
([the world](../../../Core/README.md#the-world)). The engine's `Loading` domain shows while a domain loads in place of
the world, unless the project names its own.
