# Domains

A domain is a world, as files: a folder holding a `.domain` and the chunks of entities beside it. Core ships the
default domain, `Domains/Default/`, opened when there is no entry point: a red question mark in a black void, lit by
the sun. The LoadingScreen gem ships its loading screen as one ([its README](../../../Gems/World/LoadingScreen/Resources/Domains/README.md)). Everything a
project's worlds are made of works the same way; the [Samples README](../../../../Samples/README.md#a-domain-on-disk)
walks through streaming, the loading screen, LODs and tags with examples, so this file stays to the format.

```
Domains/Default/
    Default.domain    { "chunkSize": 64 }                                    (id 702)
    globals.chunk     camera, sun, black sky, the question mark             (id 703)
```

## `.domain`

| Field | Default | What it is |
| --- | --- | --- |
| `chunkSize` | 64 | Metres a chunk covers on each axis. |

The chunks that belong to it are the `.chunk` files in its folder.

## `.chunk`

```json
{
    "entities": [
        {
            "name": "Question",
            "scripts": [ { "id": 3211, "degreesPerSecond": 20 } ],
            "components": {
                "Transform": { "rotation": { "x": 0, "y": 1, "z": 0, "w": 0 } },
                "Renderer": { "model": { "id": 1529 }, "materials": { "slot0": { "id": 714 } } }
            }
        }
    ]
}
```

That is the default domain's question mark, given a project's script to turn it (the Samples' `Turn.cs`, 3211).

| Entity field | What it is |
| --- | --- |
| `name` | Unique among its siblings, no `.`; the entity's path is its parents' names joined by dots. |
| `tags` | Any words. `static` (never moves) and `hidden` (not drawn, nor anything under it) mean something to the engine. |
| `components` | By type name (ignoring case): any struct implementing `IComponent` in the engine or a loaded gem, as its own JSON. The full name (`Namespace.Type`) when two types share a name. |
| `scripts` | `{ "id": <script asset>, <property>: value, … }`: see [Scripts](../../../Gems/World/LoadingScreen/Resources/Scripts/README.md). |
| `children` | Entities under this one; their transforms are relative to it. |

A chunk named `x_y_z.chunk` (`0_0_0`, `-1_0_3`) is a cell: it covers `[x * chunkSize, (x + 1) * chunkSize)` on each
axis and is streamed in and out by the cameras. Any other name is global: spawned when the domain opens and kept
while it is open.

## How the engine uses it

- **Opening.** The project's `"entryPoint"` (or `--entry-point`) opens at start-up, else the default domain (also
  when the one named does not open); `World.Open` from a script, or
  `portal Name` in the console, opens another in place of the world; `summon Name.domain` or `portal Name additive`
  opens one on top. A domain opened in place of the others fills behind the loading domain and appears whole.
- **Loading domain.** The LoadingScreen gem's choice: see [its README](../../../Gems/World/LoadingScreen/Resources/Domains/README.md#which-loading-domain).
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

- Component names match a C# struct in the engine or a loaded gem; an unknown one is warned about once and skipped.
- Its JSON matches the struct's fields (camelCase; handles as `{ "id" }`).
- Script ids name `.cs` assets whose class is named like the file.
- `Renderer` model and material ids exist (a missing one draws as a failure, not an error).

## Adding a domain

In a project: a folder under `Assets/Domains/` with `<Name>.domain` and its chunks (`dotnet new magicsample` makes
one). Name it as the project's `"entryPoint"`, or open it with `portal <Name>`.
