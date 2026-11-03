# WorldRenderer

Draws the world: every renderer's model, GPU-driven, lit by the sun, into the window, and to a PNG when the console
asks for a screenshot.

To use it, list `WorldRenderer` in a project's `.magic` file after the GPU provider, the shader compiler, the ECS, the
model reader, the mesh preprocessor and the texture reader.

- `Update` reads the renderers, the camera that draws into the window and the first directional light out of the
  entities. `Render` draws from that copy.
- A model is drawn once the preprocessor has made its clusters and they are in the geometry pools; the pools hold
  what fits and keep it for the run. A model that does not load is drawn as the failure model, a material that does
  not load, names the failure shader or a texture that does not load as the magenta failure material.
- Every instance keeps its row of the instance table from frame to frame, so a still scene uploads nothing; the
  camera's position is taken off on the GPU.
- Once a model and its materials are resident, the renderer captures them from 64 directions into atlases of albedo,
  normal and depth; from far enough that the whole model is under the error threshold, the instance draws as one
  quad facing the camera, read from the capture nearest its direction and lit like any surface.
- Every frame, the GPU culls each instance's clusters against the frustum, their normal cones, the pixel error
  threshold that picks the level of detail, and last frame's depth pyramid, draws the visible ones into a
  visibility buffer, retests what the old pyramid hid against the new one and draws those too. The visibility
  buffer resolves per pixel through the material contract into the GBuffer, which the sun lights.
- The sun's shadow is a virtual shadow map: a clipmap of levels of pages around the camera, each level's texels
  twice the last's. The depth buffer marks the pages its pixels need at the level their footprint picks, pages come
  from a pool when first marked and go back when no longer marked, an instance that moves invalidates the pages under
  it, and only invalid pages are drawn, by the cluster path into one pass over the pool; a still scene draws none.
- Every point, spot and rect light is binned into the froxels of the view it reaches, and a pixel loops over its
  froxel's lights alone, in physical units: lux for the sun, candela for the rest, with the exposure set so a surface
  facing the sun lands near white. The sky entity's material names the sky, which draws the sky, colours the sun
  through the air and lights the ground from above; the fog entity fills the froxels with air the sun, through its
  pages, and the lights scatter towards the camera. The picture is lit in HDR and tonemapped to the output.
- The passes are the `Shaders/WorldRenderer/` modules in the gem's `Resources`; they build on the shader library
  (`src/Resources/Shaders/README.md`).
