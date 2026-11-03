# WorldRenderer

Draws the world: every renderer's model, GPU-driven, lit by the sun, into the window, and to a PNG when the console
asks for a screenshot.

To use it, list `WorldRenderer` in a project's `.magic` file after the GPU provider, the shader compiler, the ECS, the
model reader and the mesh preprocessor.

- `Update` reads the renderers, the camera that draws into the window and the first directional light out of the
  entities. `Render` draws from that copy.
- A model is drawn once the preprocessor has made its clusters and they are in the geometry pools; the pools hold
  what fits and keep it for the run.
- Every frame, the GPU culls each instance's clusters against the frustum, their normal cones, the pixel error
  threshold that picks the level of detail, and last frame's depth pyramid, draws the visible ones into a
  visibility buffer, retests what the old pyramid hid against the new one and draws those too. The visibility
  buffer resolves per pixel through the material contract into the GBuffer, which the sun lights.
- The passes are the `Shaders/WorldRenderer/` modules in the gem's `Resources`; they build on the shader library
  (`src/Resources/Shaders/README.md`).
