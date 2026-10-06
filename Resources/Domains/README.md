# Domains

A domain is a world, as files: a folder holding a `.domain` and the chunks of entities beside it. The engine ships
one, `Loading/`: what is shown while another domain fills. Everything a project's worlds are made of works the same
way; the [Samples README](../../Samples/README.md#a-domain-on-disk) walks through streaming, the loading screen,
LODs and tags with examples, so this file stays to the format.

```
Domains/Loading/
    Loading.domain    { "chunkSize": 64 }                                    (id 700)
    globals.chunk     camera, backdrop, the icon, the bar and its back      (id 701)
```

## `.domain`

| Field | Default | What it is |
| --- | --- | --- |
| `chunkSize` | 64 | Metres a chunk covers on each axis. |
| `seed` | 0 | For scripts that generate. |
| `time` | 0 | Seconds the domain starts at. |

The chunks that belong to it are the `.chunk` files in its folder.

## `.chunk`

```json
{
    "entities": [
        {
            "name": "BarFill",
            "scripts": [ { "id": 720, "width": 0.6 } ],
            "components": {
                "Transform": { "position": { "x": -0.6, "y": -0.45, "z": -0.02 }, "scale": { "x": 0.001, "y": 0.03, "z": 0.005 } },
                "Renderer": { "model": { "id": 520 }, "materials": { "slot0": { "id": 713 } } }
            }
        }
    ]
}
```

| Entity field | What it is |
| --- | --- |
| `name` | Unique among its siblings, no `.`; the entity's path is its parents' names joined by dots. |
| `tags` | Any words. `static` (never moves) and `hidden` (not drawn, nor anything under it) mean something to the engine. |
| `components` | By type name (ignoring case): any struct implementing `IComponent` in a loaded gem, as its own JSON. `Gem.Type` when two gems share a name. |
| `scripts` | `{ "id": <script asset>, <property>: value, … }`: see [Scripts](../Scripts/README.md). |
| `children` | Entities under this one; their transforms are relative to it. |

A chunk named `x_y_z.chunk` (`0_0_0`, `-1_0_3`) is a cell: it covers `[x * chunkSize, (x + 1) * chunkSize)` on each
axis and is streamed in and out by the cameras. Any other name is global: spawned when the domain opens and kept
while it is open.

## How the engine uses it

- **Opening.** The project's `"entryPoint"` (or `--entry-point`) opens at start-up; `World.Open` from a script, or
  `portal Name` in the console, opens another in place of the world; `summon Name.domain` or `portal Name additive`
  opens one on top. A domain opened in place of the others fills behind the loading domain and appears whole.
- **Loading domain.** The project's `"loading"` (or `--loading`), else `Domains/Loading/Loading.domain`.
- **Streaming.** Cell chunks are read on workers, nearest first, and their entities spawned under a time budget each
  frame, so a big world never hitches. What stays loaded is set by `Streaming.Radius`, `Streaming.ViewDist` and the
  `Chunk` budget.
- **Far chunks.** Beyond 512 m a chunk is spawned as a generated stand-in (or the chunk its `.meta` names in
  `lods`), swapped for the real one as a camera comes near.

## Lifetime

A chunk's bytes are pooled under the `Chunk` budget (1024 MB), which is also the cap on how much stays spawned.
Editing the `.domain` reopens the domain; editing a global chunk respawns the globals; editing a cell chunk reloads
that cell.

## What must agree

- Component names match a C# struct in a loaded gem; an unknown one is warned about once and skipped.
- Its JSON matches the struct's fields (camelCase; handles as `{ "id" }`).
- Script ids name `.cs` assets whose class is named like the file.
- `Renderer` model and material ids exist (a missing one draws as a failure, not an error).

## Adding a domain

In a project: a folder under `Assets/Domains/` with `<Name>.domain` and its chunks (`dotnet new magicsample` makes
one). Name it as the project's `"entryPoint"`, or open it with `portal <Name>`. A new loading screen is a domain
like any other, named as `"loading"`.
