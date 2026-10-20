# Models

Meshes. A `.fbx`, `.gltf`/`.glb` or `.obj` file is a `Model`, which the model loader turns into the engine's space and
units: metres, +Y up, +X right and the front at +Z, so a model is the size it was made at and its front faces the
entity's forward, whatever the export's scale and axes. Export with normals, which are the file's, and as one file: the
loader is handed the one file, so a `.gltf` whose buffers are files beside it does not load; use a `.glb`, or a `.gltf`
with its buffers inside it. How the loader gets from the file's axes and scale to the engine's is in
[its README](../../Gems/AssimpModelLoader/README.md).

## The `.meta`

```json
{ "id": N, "origin": { "x": -1, "y": 0, "z": 0 } }
```

`origin` is the point of the model that sits at the entity's position, `0, 0, 0` unless it says.

## What a model is

Meshes (`Vertex.Size` bytes a vertex: position, normal, tangent, uv; 32-bit indices), each drawn with the material in
the slot it names, 0 to 7: its material's place in the file's list of materials, where one past 7 is drawn with slot 7.
An entity draws a model through its `Renderer` component, which names the model and its materials by slot:

```json
"Renderer": { "model": { "id": 515 }, "materials": [ { "id": 3323 } ] }
```

## Clusters and cards

A model's `Clusters` are its triangles in small clusters at every level, finest to coarsest, which whatever draws it
cuts through cluster by cluster, so detail follows distance across the model; past them, from a distance that grows
with the model's size, its `Cards`, Core's `Card`, a flat picture of each mesh that turns to face the camera. They are
made as the model loads, by the gem that makes clusters when the project lists one, kept in the cache, and made again
when the model's file or `.meta` changes; a model that lies flat has no cards.

## Adding a model

Drop the file under the project's `Assets/`, or in this folder for an engine primitive, set `origin` if its pivot is
not where the entity should stand, and name it from a `Renderer`.
