# GIRendering

Draws the world into the window: every renderer's model, GPU-driven, lit by the sun and the sky. When the console
asks for a screenshot, it hands the frame as a texture to the gem that writes textures.

To use it, list `GIRendering` in a project's `.magic` file after the GPU provider, the ECS, the model reader and the
texture reader.

## What it draws

- As a model loads, the gem makes what it draws it as, once per change to its file, and keeps it in the cache: its
  vertices as the GPU reads them, a hierarchy of clusters of up to 128 triangles, and a signed distance field around
  it. Each level of the hierarchy groups neighbouring clusters of the one below and simplifies them, so the GPU picks
  per cluster how much detail to draw. meshoptimizer, which the kit builds from source, does the clustering and the
  simplifying.
- A model shows once it is on the GPU. One that doesn't load is drawn as the failure model, and a material or texture
  that doesn't load as the magenta failure material.
- Every model also gets an octahedral impostor atlas: 12 by 12 views over the whole sphere, 64 texels a side each,
  every texel the surface's normal, its depth, its material slot and its texture coordinates as the view sees it,
  baked on the CPU from the clusters the hierarchy draws at an atlas texel's error.
- An instance whose model has an impostor atlas draws as it once it is under 32 pixels in radius on screen, half a
  view of the atlas, so the atlas is never magnified. Over a quarter of that the clusters and the impostor share the
  pixels by a dither, so nothing pops. `MAGIC_PROTO_IMPOSTOR_PIXELS` sets the radius, and 0 draws no impostors.
- A model nothing has drawn or waited for in a minute leaves the GPU, and loads again when it is next drawn.
- As a domain replaces the world, the loading screen stays until the models, materials and textures of what comes are
  on the GPU, or failed, and nothing drawn before carries into the frames after.
- The directional light is the sun while it is above the horizon, else the moon while it is, else the first there is.
  On a GPU that traces rays it casts shadows, soft as wide as the sun's disc.
- A sky entity's material draws the sky and colours the sun through the air, and the sky lights what it shines on.
- Point, spot and rect lights are in physical units: lux for the sun, candela for the rest. A frame draws the first
  1024. On a GPU that traces rays, the first 15 lighting each tile of the screen cast shadows, soft as wide as the
  light, and `IsShadowless` doesn't stop them yet.
- Shadows are traced one ray a pixel and light, then denoised. The sun's are smoothed over frames and across the
  screen. The tile lights' are only smoothed over frames, so their soft edges stay grainy, and one can trail for a moment
  where a tile's lights change.
- Fog, glass and `PostProcessing` aren't drawn yet: a glass material draws as an opaque one.

## Settings

Set these in the console with `set GIRendering.<name>=<value>`. A wrong value logs what it takes.

- `debug` shows one part of the GBuffer alone. `final` shows the finished frame again. The names are in `DebugView`.
- `window` is `true` or `false`, and opens the Rendering window.

With a gem that provides the debug UI, F2 opens the Rendering window, which chooses the view. F5 and F6 step through the
views, and a line on screen names any view but the finished frame. A screenshot is of the renderer's picture alone,
without what other gems draw over it.

`bake` in the console loads every asset, which makes every model's clusters, then opens every domain under `Domains/`
in turn and exits, so a release ships with the caches full.

## Changing it

`GIRenderingGem.Render` is the frame in order, top to bottom: start there.

- An area is the passes of one part of the frame, each naming the shader entries the GPU provider builds, which it
  lists in its `Shaders`. It keeps only those entries and what it carries from one frame to the next, such as last
  frame's view.
- A pass is one `RenderGraph.Compute` or `RenderGraph.Draw` call saying what it reads and writes, which puts its
  barriers in. The frame is not drawn until every area's shaders are built.
- A texture, material or model asset comes from `GpuResources`, which reads it on the first ask. It asks the graph
  for the model pools, each texture and its sampler every frame, and evicts a model once nothing has asked for it in a
  while and no renderer drawn holds it. A
  renderer's instance row is built again, asking for its model, when the renderer is written and when a model
  arrives; the material rows its slots are drawn with are made again each frame.
- Each frame, an area's `Prepare` asks the graph for its own textures and buffers by description, uploads what
  goes in them in the frame's upload pass, and returns their handles. Its `Record` takes what its passes read and
  write as arguments, and `Render` hands the handles on from one area to the next. The same description under a name
  is the same resource, holding what the last frame left in it; another one, as at a new window size, makes it again,
  empty.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../Resources/Shaders/README.md)). The passes are at the folder's root, and what they share is under it:
  - `Core/Frame` is what every pass of a frame reads.
  - `Scene/` is the GPU tables and how to read them: the instance table, the pools of vertices, indices and clusters,
    what the cull leaves for the draw and the resolve, the triangle a pixel shows, and the cull's tests.
  - `Scene/Lighting` is what the lighting passes share beyond the frame: the sun, the sky's tables and the HDR picture.
  - `Scene/Lights` is the light table and the bits of each tile of the screen that say which lights reach it.
  - `Scene/Shadows` is the shadow mask: how much of the sun and of each of its tile's first lights reaches a pixel,
    and the sun's tiles the denoiser reads.
  - `Light/` is what a local light gives a point: a point, spot or rect light's direction and irradiance there.
- Parts of the lighting are adapted from another engine under the MIT licence; `THIRD-PARTY-NOTICES.md` holds it.
