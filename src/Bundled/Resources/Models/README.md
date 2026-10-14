# Models

Meshes. A `.fbx`, `.gltf`/`.glb` or `.obj` file is a `Model`, read by the model loader gem and turned into the
engine's space and units: every node's transform is baked into its meshes' vertices, in metres, +Y up, +X right and the
front at +Z. A model is the size it was made at: 1 m in Blender is 1 m here, whatever the export's scale and axes, and
its front, Blender's -Y, faces the entity's forward, +Z. Its normals are the file's, so export with them. The loader is
handed the one file, so a `.gltf` whose buffers are files beside it does not load: use a `.glb`, or a `.gltf` with its
buffers inside it.

## The `.meta`

```json
{ "id": 520, "origin": { "x": -1, "y": 0, "z": 0 } }
```

| Key | Default | What it does |
| --- | --- | --- |
| `origin` | 0, 0, 0 | The point of the model that sits at the entity's position. |

## What a model is

Meshes (48-byte vertices: position, normal, tangent, uv; 32-bit indices), each drawn with the material in the slot
it names, 0 to 7: its material's place in the file's list of materials, where one past 7 is drawn with slot 7. An entity draws a model through its `Renderer` component, which names the model and a material for
each slot:

```json
"Renderer": { "model": { "id": 514 }, "materials": { "slot0": { "id": 710 } } }
```

## Levels of detail

A model's `Clusters` are its triangles in small clusters at every level of detail, which whatever draws it cuts
through cluster by cluster, so detail follows distance across the model; past them, from a distance that grows with
the model's size, its `Cards`, Core's `Card`, a flat picture of each mesh that turns to face the camera. They are made
as the model loads, by the model level-of-detail gem when the project lists one, kept in the cache, and made again
when the model's file or `.meta` changes; a model that lies flat has no cards.

## Adding a model

Drop the file under `Assets/` (or Core's `Resources/Models/` for an engine primitive), set `origin` if its pivot is
not where the entity should stand, and name it from a `Renderer`.
