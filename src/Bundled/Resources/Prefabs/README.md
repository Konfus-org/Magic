# Prefabs

A prefab is a bundle of entities spawned together wherever it is put: a `.prefab` file. Game code puts one in the world
through the world service; a domain spawns every prefab in its folder, a cell at its corner and any other at the
domain's origin ([Domains](../Domains/README.md)); the project's player is one, named as the player in the `.magic`
file and put at a domain's spawn. Its positions are relative to where it is put, and its rotations turn with it.

A file is a list of entities, and each entity says:

- **Its name**, what messages call it.
- **Its tags**, labels by name, up to eight, given to it as a `Tags` component: `spawn` marks where a domain puts the
  player, `player` the player's camera, and any other is the game's.
- **Its components**, by type name, any component Core, the project's code or a loaded gem declares, each as its own
  JSON.
- **Its scripts**, `.cs` assets by id, up to eight, named in the entity's `Scripts` component, each one's copy run by
  every entity spawned from it. Every other key of a script entry sets the script's public property of that name.
- **Its children**, the entities under it, their transforms relative to its own.
- **A prefab it places**, whose entities go under this one, before its own children: a forest's cell places trees, each
  at a transform of its own.

The keys are the prefab loader's file type's properties, documented on it, and the files beside this README are the
examples.

Every entity is spawned as one, with its transform put under its parent's: scaled, turned, then moved by it. A prefab
can place prefabs of its own, but never itself, which fails rather than waits for ever; a prefab it places is what it is
made from, so changing a tree's prefab reads every prefab that places it again. The gem that reads prefabs makes the
file into a `Prefab`, a tree of parts the world spawns, each entity given a copy of its part's components and then of
its scripts, which start once the whole prefab is there
([the prefab loader's README](../../Gems/PrefabLoader/README.md), [the world](../../../Core/README.md#the-world)).

## What must agree

- Component names match a struct in Core, the project's code or a loaded gem, and their JSON its fields, in Core's
  dialect ([Assets](../README.md#reading-an-asset)).
- A `Renderer`'s model and material ids exist.
- Script ids exist, each a `.cs` asset declaring a class named like the file, and each other key of the entry matches
  a public settable property of that class, which is set before the script starts. The class's constructor takes its
  entity first, then the services it uses ([Scripts](../../../Core/README.md#scripts)).
- Placed prefab ids exist, and no prefab places itself, directly or through others.
