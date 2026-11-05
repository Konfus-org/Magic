# GIRendering

Draws the world into the window: every renderer's model, GPU-driven, lit by the sun, the lights and the light they
bounce. It saves a PNG when the console asks for a screenshot.

To use it, list `GIRendering` in a project's `.magic` file after the GPU provider, the shader compiler, the ECS, the
model reader, the mesh preprocessor and the texture reader.

## What it draws

- A model shows once the preprocessor has made its clusters. One that doesn't load is drawn as the failure model, and
  a material or texture that doesn't load as the magenta failure material.
- The directional light is the sun while it is above the horizon, else the moon while it is, else the first there is.
- Point, spot and rect lights are in physical units: lux for the sun, candela for the rest.
- A sky entity's material draws the sky and colours the sun through the air. A fog entity fills the view with air the
  sun and the lights scatter.
- A material that names the glass shader is drawn after the lighting, in any order. Glass casts no sun shadow of its
  own and is left out of the distance field.
- A camera's `PostProcessing` names up to four shaders implementing the pixel effect contract, which the final pass
  runs before the tonemap.
- Rays land on the triangles drawn on a GPU that traces rays, and on the distance field elsewhere.

## Settings

Set these in the console with `set GIRendering.<name>=<value>`. A wrong value logs what it takes.

- `debug` shows one part of the picture alone: what the sun lights, the distance field, the cards, the bounced light,
  the probes, the occlusion, the motion or the reflections. `none` shows the picture again. The names are in
  `DebugView`.
- `preset` trades quality for speed. The presets are in `RendererPreset`.
- `tracer` is `field` or, on a GPU that traces rays, `rayquery`.

`bake` in the console opens every domain under `Domains/` in turn and exits, so a release ships with the caches full.

## Changing it

`GIRenderingGem.Render` is the frame in order, top to bottom: start there.

- Each area declares its own passes with `GpuPipelines` and takes what it depends on through its constructor.
- A pass is one `GpuPass.Run` call saying what it reads and writes, which puts its barriers in.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../Resources/Shaders/README.md)).
