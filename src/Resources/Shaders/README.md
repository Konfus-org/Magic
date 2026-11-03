# Shaders

The shader library every renderer and every game shader builds on, in Slang. A module is one file, named by its path:
`Core/Math.slang` begins `module "Core/Math";` and is imported as `import Core.Math;`. The gem that provides the shader
compiler loads a module and its imports from the assets, so a game's `Shaders/` folder extends the library as it is.

- `Core/` is what every module needs: the heap every resource is read through (`Heap`), what a pass is handed
  (`Pass`), what a frame is handed (`Frame`), numbers, colour, packing, sampling and hashing.
- `Scene/` is the GPU tables and how to read them: the instance table, the pools of vertices, indices and clusters,
  the material table, the capture table and the imposter quad drawn from it, what the cull leaves for the draw and
  the resolve, and the cull's tests.
- `Surface/` is what a material is handed for a pixel and what it gives back.
- `Material/` is the material contract, `IMaterial`, and the materials every project has: the standard one, the one
  a broken asset is drawn with, and what an imposter's pixel is made of.
- `Shadow/` is the sun's virtual shadow map: its tables, how a page is found, and how a receiver samples it.
- `Light/` is what a local light gives a point: a point, spot or rect light's direction and irradiance there.
- `Sky/` is the sky contract, `ISky`, and the Earth's atmosphere, which gives the sky, the sun's colour through the
  air and the sky's light on the ground from three tables made each frame.
- `Fog/` is the air as a grid of froxels over the view: what each scatters and how the grid is read.
- `Scene/Field` is the global distance field's tables: the atlas of model fields, the clipmaps of bricks around the
  camera, and how a point's distance and normal are read from them.
- `Trace/` is the tracing contract, `IWorldTracer`, and the tracer that marches cones through the distance field for
  the visibility between two points.
- A contract between shaders is an interface, `I` plus a noun, and a pass that works with any implementation is
  generic over it. A game adds a material, a sky, a light or a post effect by implementing the contract in a module of
  its own and naming it in the asset that uses it.
- Warnings are errors; matrices are row-major; a technique names its paper.

[Code Standards](../../../docs/CODE_STANDARDS.md#shaders) has the rules.
