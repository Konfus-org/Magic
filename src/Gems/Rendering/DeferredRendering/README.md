# DeferredRendering

The renderer. Each frame it runs three render steps. The first keeps the world on the GPU for the gems that draw it:
the view and its targets at the picture's size, the frame's buffer and its rows, the models, materials and textures
every renderer draws with, the instances, the lights with each tile's lights, and the sky's tables. The second lights
that world into the window, deferred: the GBuffer a geometry gem draws, lit by the sun and the sky. When the console
asks for a screenshot, it hands the frame as a texture to the gem that writes textures. It holds the types the
rendering gems share. The third draws the frame's render failures over the window.

To use it, list `DeferredRendering` in a project's `.magic` file after the GPU provider, the ECS, the model reader and
the texture reader, with a gem that draws its geometry.

## What it keeps

- As a model loads, the gem makes what the GPU holds it as, once per change to its file, and keeps it in the cache:
  its vertices as the GPU reads them and each mesh's triangles, which rays and every renderer draw.
- A model shows once it is on the GPU. One that doesn't load is drawn as the failure model.
- A material that fails, or whose shader or texture fails, draws on its entity's own mesh in flashing magenta, with
  the text "Material failed" and the material's path over it.
- The view names each model on the GPU by its model index, the one its instances name, so a renderer keeps a row of
  its own for each.
- A model nothing has drawn or waited for in `Rendering.ModelKeepSec` leaves the GPU, and loads again when it is next
  drawn.
- As a domain replaces the world, the loading screen stays until the models, materials and textures of what comes,
  the effects of its post processing with their passes, and the gem's passes are on the GPU, or failed, so its first
  frame is drawn whole, its effects included. Nothing drawn before carries into the frames after.
- The view is seen through the first camera that draws into the window, at `Rendering.FieldOfView` when that is more
  than 0.
- The finished picture is `Rendering.Resolution` in size, with a window or without, and the window shows it
  stretched. At `0 0` it is the window's size, or 1280 by 720 with no window.
- The world is drawn at `Rendering.RenderScale` of that size, and a gem that upscales takes it to that size, or it is
  stretched with `Rendering.UpscaleFilter`, `Linear` or `Nearest`. At `0` it is drawn at the most that fits 1920 by
  1080.

`bake` in the console loads every asset, which makes what every gem makes of every model, then opens every domain under
`Domains/` in turn and exits, so a release ships with the caches full.

## What it draws

- A pixel no geometry was drawn into, at the far plane's depth, shows the sky.
- The directional light is the sun while it is above the horizon, else the moon while it is, else the first there is.
- A sky entity's material draws the sky and colours the sun through the air, and the sky lights what it shines on.
- Point, spot and rect lights are in physical units: lux for the sun, candela for the rest. A frame draws the first
  1024.
- A surface is shadowed by the frame's shadow mask, where a gem that traces shadows made one for the frame, and lit
  unshadowed everywhere else.
- Each frame draws a different point of each pixel and blends with the frames before it where they saw the same
  surface, so edges come out smooth and hold still. A debug view of the GBuffer shows one frame, so its edges crawl.
- A surface takes the light that reaches it indirectly from the frame's indirect light, where a gem traced it for the
  frame, and from the sky everywhere else. A surface with a roughness of 0.6 or less reflects what
  it holds of the reflections; a rougher one its diffuse light, so a closed room reflects none of the sky.
- The first `PostProcessing`'s effects run over the lit picture in order, and finish it for the screen: each is a
  material whose shader implements the pixel effect contract, with params of its own as a surface material's are. With
  no `PostProcessing`, the picture gets the engine's `Exposure` and `Agx` materials, exposed by
  `Rendering.ExposureCompensation`; an effect whose material `Rendering.DisabledEffects` names, as `Bloom Vignette`, is
  left out. A `PostProcessing` lists every effect it wants, those included, so another tonemap is another material in the tonemap's
  place. Each reads the picture the ones before it left and writes another; one that declares levels first fills each
  level of a target of its own, which one that declares a history, as the temporal anti-aliasing and the exposure do,
  keeps from frame to frame as its history. While an effect that declares jitter runs, as the temporal anti-aliasing
  does, the camera draws each frame through a different point in each pixel. An effect shows once its material and its
  passes are built, and not while one loads or when one failed.
- The `Exposure` effect meters the picture each frame from its average log luminance, as the effects before it leave
  it, and adapts to a change over about a second, as an eye does; a new domain or picture size takes its first reading
  at once. Its `compensation` param adds stops to it. The `Agx` effect is the AgX tonemap.
- The `indirectDiffuse` and `reflections` debug views put the indirect light into the picture before the post
  effects, so the effects expose and tonemap it; black where nothing traced it.
- A translucent material, such as the engine's `Glass`, draws over the lit picture instead of with the opaque
  surfaces, so it hides nothing behind it from the depth pyramid or the lighting. The nearest pane in front of each
  pixel's opaque surface lets what is behind through its colour and its clarity, and through any pane between where a
  gem builds the world as rays see it; it reflects the light around it, more at a grazing angle, and the sun lights it.
- Where a gem filled the frame's air, the picture is seen through it up to the nearest pane, or the opaque surface
  where there is none, before the effects run. Where the air has clouds, the sky shows them and they shade the sun.
- The finished picture's middle tones are shown at `Rendering.Gamma`, and it is stretched over the window with
  `Rendering.UpscaleFilter`, whose names are in `TextureFilter`; where a gem upscales it, that gem's picture is copied
  to the window instead. A screenshot is of what the window shows, at the size the picture is shown at.
- Each failure that a gem or Core tells `RenderGraph.Fail` in a frame is a line of a banner across the top of the
  window, over the picture and the debug UI: a bar that flashes in the colour of its kind, and its text. The banner
  shows eight lines at most, and the last then counts the rest. It draws also when the view was not made.

## Settings

The gem's settings are `DeferredRenderingSettings`. `get Rendering` lists those under `Rendering`, and
`set Rendering.Resolution 2560 1440` sets one; a wrong value logs what it takes. `get Debug` lists the debug view's,
as `set Debug.RenderView Normal`. `Debug.RenderView` shows one
part of the GBuffer alone, and `Final` the finished frame again; the names are in `DebugView`.
`Debug.IsRenderWindowOpen` opens the Rendering window.

With a gem that provides the debug UI, F2 opens the Rendering window, which chooses the view. F5 and F6 step through the
views, and a line on screen names any view but the finished frame. A screenshot is of the renderer's picture alone,
without what other gems draw over it.

## Changing it

`Renderer` has a `Render` for each step, each the step in order, top to bottom: start there. The first
ends in the view it hands on; the second takes it with what the other gems made.

- A texture, material or model asset comes from `GpuResources`, which reads it on the first ask. It asks the graph
  for the model pools, the mesh table on a GPU that traces rays, and each texture and its sampler every frame. The
  pools start small and grow by half, or by what a model needs, as models arrive. It evicts a model once nothing has
  asked for it in a while and no renderer drawn holds it. A renderer's instance row is built again, asking for its
  model, when the renderer is written and when a model arrives; the material rows its slots are drawn with are made
  again each frame.
- A pass is one `RenderGraph.Compute` or `RenderGraph.Draw` call saying what it reads and writes, which puts its
  barriers in. `Render` records each pass under a short comment, or calls the class that records it: `SkyLuts`,
  `LightTiles` or `PixelEffects`.
- A step draws nothing until every shader entry it runs is built: `_viewShaders` and `_shadingShaders`.
- Each frame, a pass asks the graph for its own textures and buffers by description. The same description under a
  name is the same resource, holding what the last frame left in it; another one, as at a new picture size, makes it
  again, empty.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../../Resources/Shaders/README.md)). The passes are at the folder's root. The renderer's tables and
  techniques, which the other rendering gems import too, are in the folders under it.
- Parts of the lighting are adapted from another engine under the MIT licence;
  `THIRD-PARTY-NOTICES.md` holds it.
