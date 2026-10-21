# Domains

A domain is a piece of the world that opens as one: a folder holding a `.domain` file and the prefabs it spawns beside
it. The `.domain` file holds the domain's own settings, today only the cell size, the metres a cell covers on each
axis; the keys and their defaults are the `Domain` type's properties, documented on it, and the domains beside this
README are the examples.

A project's domain is a folder under its `Assets/` with `<Name>.domain` and its prefabs, named as the project's entry
point in the `.magic` file, which opens on the first frame, or opened by game code through the world service. The
engine's own open when a project names none: the default domain in place of an entry point, and the loading domain
while another domain replaces the world, which a project makes its own by naming one as its loading domain.

## Its prefabs

Every [prefab](../Prefabs/README.md) in the domain's folder is spawned while it is open. Where depends on its name:

- **Cells**, named `x_y_z.prefab` (`0_0_0`, `-1_0_3`): a cell covers one cell size from its corner on each axis, and
  its positions are relative to its corner, its coordinates times the cell size. A cell file can be copied or moved to
  another cell as it is. Cells are how a large world is split into files a person can work on, one piece at a time.
- **Any other**, such as `globals.prefab`, at the domain's origin: what belongs to the whole world, such as the sky,
  the sun, the weather, the world's post-processing and the spawn.

**The spawn** is the first entity tagged `spawn` in the domain's prefabs that are not cells, else in its cells: where
the project's player is put, and which way it faces, when the domain opens in place of the world. With none, the player
is put at the domain's origin.

How a domain is opened, read and spawned whole in one frame, replaced or opened beside another, readied out of sight
before it is shown in place of the world, and what happens when a prefab of it changes on disk while it is open, is
the world's ([Core's README](../../../Core/README.md#the-world)).
