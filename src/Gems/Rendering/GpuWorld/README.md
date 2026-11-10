# GpuWorld

Keeps the world on the GPU each frame for the gems that draw it: the view and its targets at the picture's size, the
frame's buffer and its rows, the models, materials and textures every renderer draws with, the instances, the lights
with each tile's lights, and the sky's tables. The gems that draw take the view it makes.

To use it, list `GpuWorld` in a project's `.magic` file after the GPU provider, the ECS, the model reader and the
texture reader, with a gem that draws its view.

## What it keeps

- As a model loads, the gem makes what the GPU holds it as, once per change to its file, and keeps it in the cache:
  its vertices as the GPU reads them and each mesh's triangles, which rays and every renderer draw.
- A model shows once it is on the GPU. One that doesn't load is drawn as the failure model, and a material or texture
  that doesn't load as the magenta failure material.
- The view names each model on the GPU by its model index, the one its instances name, so a renderer keeps a row of
  its own for each.
- A model nothing has drawn or waited for in a minute leaves the GPU, and loads again when it is next drawn.
- As a domain replaces the world, the loading screen stays until the models, materials and textures of what comes,
  and the effects of its weather and its post processing with their passes, are on the GPU, or failed, so its first
  frame is drawn whole, its effects included. Nothing drawn before carries into the frames after.
- The effects the frame runs are the first weather's, then the first post processing's, or the engine's
  `TemporalAntiAliasing`, `Exposure` and `Agx` when nothing has one; the view names each with its material's row and
  its passes. The camera jitters while one of them asks for it, or while a gem upscales the picture.
- The picture is drawn in the window's shape, at most as many pixels across and down as `Rendering.Resolution`,
  and stretched over the window, or upscaled to it by a gem that upscales. A smaller window draws at its own size, and so does every window when it is `0 0`; a run with no window draws at 1280
  by 720.

`bake` in the console loads every asset, which makes what every gem makes of every model, then opens every domain under `Domains/`
in turn and exits, so a release ships with the caches full.

## Settings

The gem's settings are `GpuWorldSettings`, under `Rendering`. `get Rendering` lists them in the console, and
`set Rendering.Resolution 2560 1440` sets one; a wrong value logs what it takes.

## Changing it

`GpuWorldGem.Render` is the frame's upload in order, top to bottom, ending in the view it hands on.

- A texture, material or model asset comes from `GpuResources`, which reads it on the first ask. It asks the graph
  for the model pools, each texture and its sampler, and each resident model's acceleration structure every frame, and evicts a model once nothing has asked for it in a while and no renderer drawn holds it. A
  renderer's instance row is built again, asking for its model, when the renderer is written and when a model
  arrives; the material rows its slots are drawn with are made again each frame.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../../Resources/Shaders/README.md)).
