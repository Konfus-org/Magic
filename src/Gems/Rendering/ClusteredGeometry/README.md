# ClusteredGeometry

Draws every model in the world's view as a hierarchy of clusters, GPU-driven, and far away as its impostor, into the
view's depth, GBuffer and glass targets for the gems that light them.

To use it, list `ClusteredGeometry` in a project's `.magic` file after the gem that keeps the world on the GPU, whose
view it draws, with a gem that lights the GBuffer.

## What it draws

- As a model loads, the gem makes what it draws it as, once per change to its file, and keeps it in the cache: a
  hierarchy of clusters of up to 128 triangles. Each level of the hierarchy groups neighbouring clusters of the one
  below and simplifies them, so the GPU picks per cluster how much detail to draw. The leaves draw the model's own
  vertices, as the renderer holds them; each level above draws copies of the vertices it kept. meshoptimizer, which
  the kit builds from source, does the clustering and the simplifying.
- Every model also gets an octahedral impostor atlas: 12 by 12 views over the whole sphere, 64 texels a side each,
  every texel the surface's normal, its depth, its material slot and its texture coordinates as the view sees it,
  baked on the CPU from the clusters the hierarchy draws at an atlas texel's error.
- An instance whose model has an impostor atlas draws as it once it is under 32 pixels in radius on screen, half a
  view of the atlas, so the atlas is never magnified. Over a quarter of that the clusters and the impostor share the
  pixels by a dither, so nothing pops.
- A model draws once its clusters are on the GPU. As a domain replaces the world, the loading screen stays until the
  models of what comes are clustered on the GPU and the resolve pass of each material shader is built.
- A translucent cluster draws the nearest pane in front of the opaque surfaces into the glass targets. A glass instance
  far enough to draw as its impostor draws opaque.

`bake` in the console loads every asset, which clusters every model.

## Settings

`Rendering.ImpostorPixels` and `Rendering.ImpostorDistance`, which `get Rendering` lists with what each does, choose
when a model draws as its impostor.

- When the frame's clusters near the gem's limit, it raises both tests on its own, then lets cluster detail coarsen,
  until they fit.

## Changing it

`ClusteredGeometryGem.Render` is the frame in order, top to bottom.

- `ClusterResources` keeps a row for each row of the view's model table: when a row's model changes, it reads the new
  model's clusters from the cache and copies them into its own pools, and gives the old one's ranges back. A pool
  starts small, and a model that doesn't fit grows it by half; the GPU provider keeps what it held.
- `ClusterPasses` records the passes: the cluster budget, the cull, the draws into the visibility buffer, the depth
  pyramid, the retest, the glass and the per-material resolve.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../../Resources/Shaders/README.md)). The passes are at the folder's root, and what they share is under it:
  - `Scene/` is the cluster renderer's tables and how to read them: the cluster pool, each model's clusters and
    impostor by its model index, what the cull leaves for the draw and the resolve, the triangle a pixel shows, and the
    cull's tests.
  - `Scene/Clusters` is also what the cluster passes read beyond the library's frame: the cull's limits and buffers,
    the depth pyramid, the visibility ids and the impostor handoff, in the frame's buffer after the air.
