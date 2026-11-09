# Deferred

Draws the world into the window, deferred: every renderer's model, GPU-driven, lit by the sun and the sky, from the
view the gem that keeps the world on the GPU makes each frame. When the console asks for a screenshot, it hands the
frame as a texture to the gem that writes textures.

To use it, list `Deferred` in a project's `.magic` file beside the gem that keeps the world on the GPU.

## What it draws

- An instance whose model has an impostor atlas draws as it once it is under 32 pixels in radius on screen, half a
  view of the atlas, so the atlas is never magnified. Over a quarter of that the clusters and the impostor share the
  pixels by a dither, so nothing pops.
- The directional light is the sun while it is above the horizon, else the moon while it is, else the first there is.
- A sky entity's material draws the sky and colours the sun through the air, and the sky lights what it shines on.
- Point, spot and rect lights are in physical units: lux for the sun, candela for the rest. A frame draws the first
  1024.
- A surface is shadowed by the frame's shadow mask, where a gem that traces shadows made one for the frame, and lit
  unshadowed everywhere else.
- Each frame draws a different point of each pixel and blends with the frames before it where they saw the same
  surface, so edges come out smooth and hold still. A debug view of the GBuffer shows one frame, so its edges crawl.
- A surface takes the light that reaches it indirectly from the frame's indirect light, where a gem that traces it
  wrote it for the frame, and from the sky everywhere else. A surface with a roughness of 0.6 or less reflects what
  it holds of the reflections; a rougher one its diffuse light, so a closed room reflects none of the sky.
- The first `Weather`'s effects, such as the fog, and then the first `PostProcessing`'s run over the lit picture in
  order, and finish it for the screen: each is a material whose shader implements the pixel effect contract, with
  params of its own as a surface material's are. With no `PostProcessing`, the picture gets the engine's
  `TemporalAntiAliasing`, `Exposure` and `Agx` materials; a `PostProcessing` lists every effect it wants, those
  included, so another tonemap is another material in the tonemap's place. Each reads the picture the ones before it
  left and writes another; one that declares levels first fills each level of a target of its own, which it keeps from
  frame to frame as its history. While an effect that declares jitter runs, as the temporal anti-aliasing does, the
  camera draws each frame through a different point in each pixel. An effect shows once its material and its passes
  are built, and not while one loads or when one failed. An effect that asks what lies along a ray, as the volumetric
  light does, is answered through the world as rays see it, which lets all the light through where no gem builds it,
  and doesn't run on a GPU that doesn't trace rays.
  The fog thickens with the air, and the volumetric light gathers its shafts, up to the nearest surface, a pane of glass
  included, so a pane keeps its colour, and what is seen through a pane is fogged only as far as the pane. The fog is lit by the sun and the sky alone,
  unshadowed; with the fog's `sun` param at 0 and `VolumetricLight` listed, the sun's light in the fog comes only where
  the air sees the sun, in shafts.
- The `Exposure` effect meters the picture each frame from its average log luminance, as the effects before it leave
  it, and adapts to a change over about a second, as an eye does; a new domain or window size takes its first reading
  at once. Its `compensation` param adds stops to it. The `Agx` effect is the AgX tonemap.
- The `indirectDiffuse` and `reflections` debug views put the indirect light into the picture before the post
  effects, so the effects expose and tonemap it; black where nothing traced it.
- A translucent material, such as the engine's `Glass`, draws over the lit picture instead of with the opaque
  surfaces, so it hides nothing behind it from the depth pyramid or the lighting. The nearest pane in front of each
  pixel's opaque surface lets what is behind through its colour and its clarity, and through any pane between where a
  gem builds the world as rays see it; it reflects the light around it, more at a grazing angle, and the sun lights it. A glass
  instance far enough to draw as its impostor draws opaque.

## Settings

Set these in the console with `set Deferred.<name>=<value>`. A wrong value logs what it takes.

- `debug` shows one part of the GBuffer alone. `final` shows the finished frame again. The names are in `DebugView`.
- `window` is `true` or `false`, and opens the Rendering window.
- `impostorPixels` is the radius on screen, in pixels, under which a model draws as its impostor. 0 turns that test
  off.
- `impostorDistance` is the distance in metres past which a model draws as its impostor. 0 turns that test off.
- When the frame's clusters near the renderer's limit, it raises both tests on its own, then lets cluster detail
  coarsen, until they fit.

With a gem that provides the debug UI, F2 opens the Rendering window, which chooses the view. F5 and F6 step through the
views, and a line on screen names any view but the finished frame. A screenshot is of the renderer's picture alone,
without what other gems draw over it.

## Changing it

`DeferredGem.Render` is the frame in order, top to bottom: start there. As a domain loads, the loading screen also
waits for each material shader's resolve pass.

- An area is the passes of one part of the frame, each naming the shader entries the GPU provider builds, which it
  lists in its `Shaders`. It keeps only those entries and what it carries from one frame to the next, such as last
  frame's view.
- A pass is one `RenderGraph.Compute` or `RenderGraph.Draw` call saying what it reads and writes, which puts its
  barriers in. The frame is not drawn until every area's shaders are built.
- Each frame, an area's `Prepare` asks the graph for its own textures and buffers by description, uploads what
  goes in them in the frame's upload pass, and returns their handles. Its `Record` takes what its passes read and
  write as arguments, and `Render` hands the handles on from one area to the next. The same description under a name
  is the same resource, holding what the last frame left in it; another one, as at a new window size, makes it again,
  empty.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../../Resources/Shaders/README.md)). The passes are at the folder's root, and what they share is under it:
  - `Scene/` is the cluster renderer's tables and how to read them: the cluster pool, each model's clusters and
    impostor by its model index, what the cull leaves for the draw and the resolve, the triangle a pixel shows, and the
    cull's tests.
  - `Scene/Clusters` is also what the cluster passes read beyond the library's frame: the cull's limits and buffers,
    the depth pyramid, the visibility ids and the impostor handoff, in the frame's buffer after the library's models,
    lights and shadows.
- Parts of the lighting are adapted from another engine under the MIT licence;
  `THIRD-PARTY-NOTICES.md` holds it.
