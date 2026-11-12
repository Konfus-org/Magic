# Shaders

The shader library every renderer and every game shader builds on, in Slang. A module is one file, named by its path:
`Core/Math.slang` begins `module "Core/Math";` and is imported as `import Core.Math;`. The gem that provides the shader
compiler loads a module and its imports from the assets, so a game's `Shaders/` folder extends the library as it is.

- `Core/` is what every module needs: the heap every resource is read through (`Heap`), what a pass is handed
  (`Pass`), what every pass of a frame reads (`Frame`), numbers, colour, packing, sampling and hashing.
- `Surface/` is what a material is handed for a pixel and what it gives back.
- `Material/` is the material contract, `IMaterial`, and the materials every project has: the standard one and glass,
  which is translucent.
- `Sky/` is the sky contract, `ISky`.
- `Post/` is the pixel effect contract, `IPixelEffect`, which post effects implement, a vignette, a blur, the temporal
  anti-aliasing, the metered exposure, a bloom and the AgX tonemap. Where an effect runs is its place in the list, so
  one before the exposure works in light as it is, one after it in exposed light and one after the tonemap in the
  screen's. An effect that needs a target of its own declares levels of it, which the blur shows, each of which may read
  the one before it, as the bloom's chain of ever blurrier halves does; one that reads last frame's also declares a
  history, which the exposure shows; one that blends frames declares jitter, which the temporal anti-aliasing shows. An
  effect reads the frame and its lights itself.
- A contract between shaders is an interface, `I` plus a noun, and a pass that works with any implementation is
  generic over it. A game adds a material, a sky or a post effect by implementing the contract in a module of its own
  and naming it in the asset that uses it.
- Warnings are errors; matrices are row-major; a technique names its paper.
- What is adapted from another engine is under its licence, which `THIRD-PARTY-NOTICES.md` holds.

A renderer's own tables and techniques are in its gem's `Resources/Shaders/`.

[Shader Standards](../../../docs/SHADER_STANDARDS.md) has the rules.
