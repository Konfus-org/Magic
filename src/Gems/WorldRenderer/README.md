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
- The world is also kept as a signed distance field: every model's field, made by the preprocessor, goes into an atlas
  as it arrives, and four clipmaps of bricks around the camera are made again from the instances over them only where
  the camera moved or an instance changed. The strongest lights of a pixel's froxel trace cones through the field for
  their shadow. `set WorldRenderer.debug=field` in the console shows the field at every surface instead of the
  picture; `set WorldRenderer.debug=none` shows the picture again.
- Light bounces: the instances that take most of the view hold lit cards, their captures lit by the sun through its
  pages, the lights that reach them and the world probes, a few cards a frame; world probes around the camera trace
  the field and read the cards where their rays land, the sky where they miss; a screen probe in every tile of
  pixels traces the screen, then the field, then reads the world probes, and the pixels read the probes around them
  on their plane. A room with no window stays dark. `set WorldRenderer.debug=indirect` shows that light alone and
  `set WorldRenderer.debug=probes` what the world probes hold at every surface.
- A smooth surface mirrors the world: a ray a pixel at half the output, drawn from the surface's lobe, traces the
  screen, then the field, then the world probes, so a floor shows the wall behind the camera; the rays are gathered
  over a reach the roughness sets and blended with last frame's, held within what the neighbours see.
  `set WorldRenderer.debug=reflections` shows the reflections alone.
- A material that names the glass shader is drawn after the lighting, forward, in any order: the panes' absorbance
  and the moments of their depths are added up first, then each pane's light, the sun through its pages and the
  other panes, the lights of its froxel and the screen probes' reflection, weighed by what reaches it; the tonemap
  puts them over the picture, bent by their normals. The sun through the panes colours what is behind them, and a
  ray through them arrives tinted. Glass casts no sun shadow of its own and is left out of the distance field.
- The picture is drawn at a share of the output and resolved into it over time: each frame's projection is shifted
  by a jittered pixel, and the resolve blends the frame with last frame's picture reprojected onto the same surfaces,
  held within what the neighbours show. The exposure is metered from a histogram of the picture, the brightest of it
  blooms, and the final pass runs the camera's effects, up to four shaders implementing the pixel effect contract
  named in its `PostProcessing`, and tonemaps with AgX. `set WorldRenderer.preset=toaster`, `balanced` or `quality`
  sets the share, how many cards are lit a frame and whether reflections are traced.
- On a GPU that traces rays, every model's leaf clusters go into an acceleration structure as it arrives and the
  instances into one over them, built again when any changes, and the probes, the reflections and the lights'
  shadows trace through them instead of the distance field, landing exactly on the triangles that are drawn.
  `set WorldRenderer.tracer=field` traces through the field instead; `rayquery` goes back.
- `bake` in the console opens every domain under `Domains/` in turn and exits, so a release ships with the caches
  full.
- The passes are the `Shaders/WorldRenderer/` modules in the gem's `Resources`; they build on the shader library
  (`src/Resources/Shaders/README.md`).
