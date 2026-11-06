# GIRendering

Draws the world into the window: every renderer's model, GPU-driven, lit by the sun and the sky. When the console
asks for a screenshot, it hands the frame as a texture to the gem that writes textures.

To use it, list `GIRendering` in a project's `.magic` file after the GPU provider, the shader compiler, the ECS, the
model reader, the mesh preprocessor and the texture reader.

## What it draws

- A model shows once the preprocessor has made its clusters. One that doesn't load is drawn as the failure model, and
  a material or texture that doesn't load as the magenta failure material.
- The directional light is the sun while it is above the horizon, else the moon while it is, else the first there is.
  On a GPU that traces rays it casts shadows, soft as wide as the sun's disc.
- A sky entity's material draws the sky and colours the sun through the air, and the sky lights what it shines on.
- Point, spot and rect lights are in physical units: lux for the sun, candela for the rest. A frame draws the first
  1024. On a GPU that traces rays, the first 15 lighting each tile of the screen cast shadows, soft as wide as the
  light, and `IsShadowless` doesn't stop them yet.
- Shadows are traced one ray a pixel and light, so their soft edges are noisy.
- Fog, glass and `PostProcessing` aren't drawn yet: a glass material draws as an opaque one.

## Settings

Set these in the console with `set GIRendering.<name>=<value>`. A wrong value logs what it takes.

- `debug` shows one part of the GBuffer alone. `final` shows the finished frame again. The names are in `DebugView`.
- `window` is `true` or `false`, and opens the Rendering window.

With a gem that provides the debug UI, F2 opens the Rendering window, which chooses the view. F5 and F6 step through the
views, and a line on screen names any view but the finished frame. With a gem that provides `IOverlay`, it is drawn over
the finished picture.

`bake` in the console opens every domain under `Domains/` in turn and exits, so a release ships with the caches full.

## Changing it

`GIRenderingGem.Render` is the frame in order, top to bottom: start there.

- Each area declares its own passes with `GpuPipelines` and takes what it depends on through its constructor.
- A pass is one `GpuPass.Run` call saying what it reads and writes, which puts its barriers in.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../Resources/Shaders/README.md)). The passes are at the folder's root, and what they share is under it:
  - `Core/Frame` is what every pass of a frame reads.
  - `Scene/` is the GPU tables and how to read them: the instance table, the pools of vertices, indices and clusters,
    what the cull leaves for the draw and the resolve, and the cull's tests.
  - `Scene/Lighting` is what the lighting passes share beyond the frame: the sun, the sky's tables and the HDR picture.
  - `Scene/Lights` is the light table and the bits of each tile of the screen that say which lights reach it.
  - `Scene/Shadows` is the shadow mask: how much of the sun and of each of its tile's first lights reaches a pixel.
  - `Light/` is what a local light gives a point: a point, spot or rect light's direction and irradiance there.
- Parts of the lighting are adapted from another engine under the MIT licence; `THIRD-PARTY-NOTICES.md` holds it.
