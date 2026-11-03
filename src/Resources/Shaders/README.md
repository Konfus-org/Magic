# Shaders

The shader library every renderer and every game shader builds on, in Slang. A module is one file, named by its path:
`Core/Math.slang` begins `module "Core/Math";` and is imported as `import Core.Math;`. The gem that provides the shader
compiler loads a module and its imports from the assets, so a game's `Shaders/` folder extends the library as it is.

- `Core/` is what every module needs: the heap every resource is read through (`Heap`), what a pass is handed
  (`Pass`), numbers, colour, packing, sampling and hashing.
- A contract between shaders is an interface, `I` plus a noun, and a pass that works with any implementation is
  generic over it. A game adds a material, a sky, a light or a post effect by implementing the contract in a module of
  its own and naming it in the asset that uses it.
- Warnings are errors; matrices are row-major; a technique names its paper.

[Code Standards](../../../docs/CODE_STANDARDS.md#shaders) has the rules.
