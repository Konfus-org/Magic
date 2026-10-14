# Domains

A domain is a piece of the world that opens as one: a folder holding a `.domain` file and the prefabs it spawns beside
it.

- **The default domain** (`Default<Domain>`) opens when the project's entry point does not.
- **The loading domain** is shown while another domain replaces the world: the project's own (`"loading"` in the
  `.magic` file), else the engine's. A project makes its transitions its own by naming one.

## `.domain`

| Field | Default | What it is |
| --- | --- | --- |
| `cellSize` | 64 | Metres a cell covers on each axis. |

## Its prefabs

Every [prefab](../Prefabs/README.md) in the domain's folder is spawned while it is open. Where depends on its name:

- **Cells**, named `x_y_z.prefab` (`0_0_0`, `-1_0_3`): a cell covers `[x * cellSize, (x + 1) * cellSize)` on each
  axis, and its positions are relative to its corner, `(x, y, z) * cellSize`. A cell file can be copied or moved to
  another cell as it is. Cells are how a large world is split into files a person can work on, one piece at a time.
- **Any other**, such as `globals.prefab`, at the domain's origin: what belongs to the whole world, such as the sky,
  the sun, the world's post-processing and the spawn.

**The spawn** is the first entity tagged `spawn` in the domain's prefabs that are not cells, else in its cells: where
the project's player is put, and which way it faces, when the domain opens in place of the world. With none, the player
is put at the domain's origin.

## How the engine uses it

Through the world ([Core's README](../../../Core/README.md#the-world)).

- **Opening.** The project's `"entryPoint"` opens on the first frame, else the default domain. `World.OpenAsync` opens
  another in place of the open ones (`Replace`) or beside them (`Additive`).
- **Loading.** Every prefab of the domain is read, with the models it draws, then all of it is spawned in one frame,
  with the player at the spawn. A `Replace` closes the open domains and shows the loading domain until then, and the
  loading domain closes in that frame, so no frame shows both.
- **Nothing is streamed.** The whole domain is there while it is open; whatever draws it picks each thing's detail by
  its distance, so far cells cost little.
- **Editing.** A prefab that changes on disk is spawned again as it is now; prefabs that come or go appear or
  disappear.

## Adding a domain

In a project: a folder under `Assets/Domains/` with `<Name>.domain` and its prefabs. Name it as the project's
`"entryPoint"`, or open it with `World.OpenAsync`.
