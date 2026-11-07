# Shaders

The shader library every renderer and every game shader builds on, in Slang. A module is one file, named by its path:
`Core/Math.slang` begins `module "Core/Math";` and is imported as `import Core.Math;`. The gem that provides the shader
compiler loads a module and its imports from the assets, so a game's `Shaders/` folder extends the library as it is.

- `Core/` is what every module needs: the heap every resource is read through (`Heap`), what a pass is handed
  (`Pass`), numbers, colour, packing, sampling and hashing.
- `Scene/Materials` is the material table's row, which a material is handed.
- `Surface/` is what a material is handed for a pixel and what it gives back.
- `Material/` is the material contract, `IMaterial`, and the materials every project has: the standard one, the one
  a broken asset is drawn with, and glass.
- `Sky/` is the sky contract, `ISky`, and the Earth's atmosphere, which gives the sky, the sun's colour through the
  air and the sky's light on the ground from three tables made each frame.
- `Post/` is the post effect contract, `IPixelEffect`, the effect that does nothing and a vignette.
- `Weather/` is what the weather's effects are drawn with: the fog, whose fields are a fog material's params.
- A contract between shaders is an interface, `I` plus a noun, and a pass that works with any implementation is
  generic over it. A game adds a material, a sky or a post effect by implementing the contract in a module of its own
  and naming it in the asset that uses it.
- Warnings are errors; matrices are row-major; a technique names its paper.

A renderer's own tables and techniques are in its gem's `Resources/Shaders/`.

[Code Standards](../../../docs/CODE_STANDARDS.md#shaders) has the rules.
