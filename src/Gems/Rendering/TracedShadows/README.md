# TracedShadows

Shadows each pixel by tracing rays through the world as rays see it, for the renderer's shading. Without it, or on a
GPU that doesn't trace rays, or with `Rendering.IsTracedShadowsOn` off, nothing is shadowed.

To use it, list `TracedShadows` in a project's `.magic` file with a gem that builds the world as rays see it.

## What it draws

- The sun casts shadows, soft as wide as its disc, and the first 15 point, spot and rect lights lighting each tile of
  the screen cast shadows soft as wide as the light. `IsShadowless` doesn't stop them yet.
- A shadow ray passes glass, and anything else whose material's alpha is under 1, by its colour times what its alpha
  doesn't hold back, so a window lets the sun in. While the view draws glass, the sun's light through it takes on its
  colour; otherwise, and for a tile light, it takes how much gets through, uncoloured.
- Shadows are traced and denoised at `Rendering.ShadowScale` of the picture's size along each side, then read back
  at the picture's size along its surfaces. `set Rendering.ShadowScale 1` sets it live. A smaller scale is faster
  and softens small shadows.
- Shadows are traced one ray a pixel and light, then denoised. The sun's are smoothed over frames and across the
  screen, blurred `Rendering.ShadowFilterPasses` times before a last blur; one is faster and grainier. The tile
  lights' are only smoothed over frames, so their soft edges stay grainy, and one can trail for a moment where a
  tile's lights change.

## How it works

- Its `Render` records the passes in the frame's light stage, writes the shadows' ids into the frame's buffer as the
  library's `Scene/Shadows`, which shading reads, and hands the frame's `ShadowMask` to the gems that take it.
- The shaders are the `Shaders/` modules in this gem's `Resources`: `TraceShadows` traces, `DenoiseShadows` classifies
  the sun's tiles, filters them, blends with the frame before and brings the mask to the picture's size.
