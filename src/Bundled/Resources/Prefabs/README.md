# Prefabs

A prefab is a bundle of entities spawned together wherever it is put: a `.prefab` file. Game code puts one in the world
with `World.Spawn`; a domain spawns every prefab in its folder, a cell at its corner and any other at the domain's
origin ([Domains](../Domains/README.md)); the project's player is one, named by `"player"` in the `.magic` file and put
at a domain's spawn. Its positions are relative to where it is put, and its rotations turn with it.

```json
{
    "entities": [
        {
            "name": "Camera",
            "tags": [ "player" ],
            "components": {
                "Transform": { "position": { "x": 0, "y": 1.7, "z": 0 } },
                "Camera": {}
            },
            "scripts": [
                { "id": 3315, "speed": 30 }
            ]
        }
    ]
}
```

| Entity field | What it is |
| --- | --- |
| `name` | What messages call it. |
| `tags` | Labels by name, up to eight, given to it as a `Tags` component: `spawn` marks where a domain puts the player, `player` the player's camera, and any other is the game's. |
| `components` | By type name, any component Core or a loaded gem declares, each as its own JSON. |
| `scripts` | Scripts (`.cs` assets) by id, each one's copy run by every entity spawned from it: `[ { "id": N, "height": 1 } ]`. Every key beside `id` sets the script's public property of that name. |
| `children` | The entities under it, their transforms relative to its own. |
| `prefab` | Another prefab, whose entities go under this one, before its own `children`: a forest's cell places trees, `{ "prefab": { "id": 3344 }, "components": { "Transform": { "position": { "x": 12, "y": 0, "z": 40 }, "scale": { "x": 1.2, "y": 1.2, "z": 1.2 } } } }`. |

Every entity is spawned as one, with its transform put under its parent's: scaled, turned, then moved by it. A prefab
can place prefabs of its own, but never itself, which fails rather than waits for ever. The samples build trees,
towers, houses, hills, mountains and lakes from the engine's basic shapes this way, and a cell is mostly prefabs
placed.

## How the engine reads it

The gem that reads prefabs makes each file into a `Prefab`: a tree of parts, each entity a `PrefabPart` holding its
components and its scripts as their own types and the parts under it, a prefab it places among them, shared rather
than copied. Nothing past it sees the JSON. Spawning (`Prefab.Spawn`) makes every entity, each with a copy of its part's
components, then gives each a copy of its part's scripts, which start once the whole prefab is there. A prefab it
places is what it is made from, so changing a tree's prefab reads every prefab that places it again.

## What must agree

- Component names match a struct in Core or a loaded gem, and their JSON its fields (camelCase; handles as
  `{ "id" }`).
- `Renderer` model and material ids exist.
- Script ids exist, and each override key matches a public property of the script with a setter.
- Placed prefab ids exist, and no prefab places itself, directly or through others.
- A script's class has one public constructor that takes services, never its entity, which it is given in `Start`.
