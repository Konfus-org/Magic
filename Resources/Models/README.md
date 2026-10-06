# Models

Meshes. Anything Assimp reads (`.fbx`, `.gltf`/`.glb`, `.obj`, …) is a `Model`, loaded by the AssimpModels gem and
turned into the engine's space and units. The engine's primitives:

| Model | Id | | Model | Id |
| --- | --- | --- | --- | --- |
| `Capsule.fbx` | 512 | | `Sphere.fbx` | 517 |
| `Cone.fbx` | 513 | | `Torus.fbx` | 518 |
| `Cube.fbx` | 514 | | `Plane.fbx` | 519 |
| `Cylinder.fbx` | 515 | | `Bar.fbx` | 520 (origin at its left end: the loading bar grows from it) |
| `Monkey.fbx` | 516 | | `Question.fbx` | 1529 |

## The `.meta`

```json
{ "id": 520, "version": 1, "origin": { "x": -1, "y": 0, "z": 0 } }
```

| Key | Default | What it does |
| --- | --- | --- |
| `origin` | 0, 0, 0 | The point of the model that sits at the entity's position. |
| `lods` | none | Authored levels of detail: `{ "0.25": 1234 }`, the model to draw instead once this one is under that share of the screen's height. |

## What a model is

Meshes (48-byte vertices: position, normal, tangent, uv; 32-bit indices) and parts, each a mesh with the material
slot it draws with. An entity draws a model through its `Renderer` component, which names the model and a material
for each slot:

```json
"Renderer": { "model": { "id": 514 }, "materials": { "slot0": { "id": 710 } } }
```

A part's slot past 7 draws with slot 7; a slot with no material draws with the default material.

## Levels of detail

A model without authored `lods` gets generated ones, by the MeshLods gem: simplified meshes at 10%, 4% and 1.5% of
the screen's height, then an impostor (a camera-facing card baked from eight directions) at 0.6%. They are made on a
worker the first time the model loads, kept in `Cache/Lods/Model/`, and made again when the model or the generator
changes. `Lod.Bias` moves every threshold.

## Lifetime

Loaded when the first entity using it registers, its meshes uploaded once and shared by every instance, and freed
when the last goes. Pooled under the `Model` budget (256 MB). **Not reloaded live**: an edited model warns that its
entities need removing and adding again. A model that does not load draws as the glowing failure cube.

## Adding a model

Drop the file under `Assets/` (or `Resources/Models/` for the engine), set `origin` if its pivot is not where the
entity should stand, and name it from a `Renderer`. `summon Name.fbx` in the console puts it in front of the camera.
Name the material slots in the modelling tool in the order you want them; slot 0 is the first material.
