# Shaders

The shader library every renderer and every game shader builds on, in Slang. A module is one file, named by its path:
`Core/Math.slang` begins `module "Core/Math";` and is imported as `import Core.Math;`. The gem that provides the shader
compiler loads a module and its imports from the assets, so a game's `Shaders/` folder extends the library as it is.

- `Core/` is what every module needs: the heap every resource is read through (`Heap`), what a pass is handed
  (`Pass`), what every pass of a frame reads (`Frame`), numbers, colour, packing, sampling and hashing.
- `Scene/Materials` is the material table's row: which material shader draws it, then that shader's struct.
- `Scene/Models` is the frame's models, in the frame's buffer after the frame: the instance table, the pools of
  vertices and indices the instances are drawn from, the material table, and on a GPU that traces rays the world's
  acceleration structure, with a geometry for each mesh of each model, and the mesh table that says where each mesh's
  triangles are and which material slot they take. `Scene/Instances` and `Scene/Vertices` are an instance's and a
  vertex's rows. `Trace/HitSurface` is what rays read of the world through them: how much light gets through along a
  ray, and the surface one lands on.
- `Scene/Lights` is the frame's lights, in the frame's buffer after the models: the sun, the sky's tables, and the light
  table with the bits of each tile of the screen that say which lights reach it. `Scene/Shadows`, after them, is the
  shadow mask: how much of the sun and of each of its tile's first lights reaches a pixel, the sun's tiles the denoiser
  reads and the colour the sun takes on through glass.
- `Surface/` is what a material is handed for a pixel and what it gives back.
- `Material/` is the material contract, `IMaterial`, and the materials every project has: the standard one, the one
  a broken asset is drawn with, and glass, which is translucent.
- `Sky/` is the sky contract, `ISky`, and the Earth's atmosphere, which gives the sky, the sun's colour through the
  air and the sky's light on the ground from three tables made each frame.
- `Post/` is the pixel effect contract, `IPixelEffect`, which post and weather effects implement, a vignette, a blur,
  the temporal anti-aliasing, the metered exposure, a bloom, the sun's god rays, the volumetric light and the AgX
  tonemap. Where an effect runs is its place in the list, so one before the exposure works in light as it is, one after
  it in exposed light and one after the tonemap in the screen's. An effect that needs a target of its own, or last
  frame's, declares levels of it, which the blur and the exposure show, each of which may read the one before it, as the
  bloom's chain of ever blurrier halves does; one that blends frames declares jitter, which the temporal anti-aliasing
  shows. An effect reads the frame and its lights itself, and asks how much light gets through along a ray with
  `Scene/Shadows`' `GetTransmittance`, and declares rays, as the volumetric light does.
- `Weather/` is the weather's effects: the fog, a pixel effect whose fields are a fog material's params.
- A contract between shaders is an interface, `I` plus a noun, and a pass that works with any implementation is
  generic over it. A game adds a material, a sky or a post effect by implementing the contract in a module of its own
  and naming it in the asset that uses it.
- Warnings are errors; matrices are row-major; a technique names its paper.
- What is adapted from another engine is under its licence, which `THIRD-PARTY-NOTICES.md` holds.

A renderer's own tables and techniques are in its gem's `Resources/Shaders/`.

[Code Standards](../../../docs/CODE_STANDARDS.md#shaders) has the rules.
