# Shader Standards

The rules for Slang shaders in Magic. [Code Standards](CODE_STANDARDS.md) covers the C# that shares their structs.

- Slang 2026, warnings as errors. One module a file, named by its path (`module "Core/Heap";`). The gem that provides
  `IShaderCompiler` compiles shaders and loads their imports from the assets.
- Read every resource through the heap's references in `Core/Heap.slang`; never declare one.
- Name a pass for its job, a verb and what it acts on (`CullClusters`, `ShadeLighting`), in its feature's module. Its
  constants are uniform parameters: the pass header, then one struct.
- Names follow [Naming](CODE_STANDARDS.md#naming):
  - A function returning a value starts with `Get` (`GetLuminance`), or `Load`/`Sample` for memory reads, `To` for
    conversions, `Encode`/`Decode` for packing and `Create` for a struct it builds. A contract's work is `Evaluate`,
    `Shade` or `Apply`. `Pow2`, `Max3` and the like read as the intrinsics beside them.
  - A bool starts with `Is`, `Has` or `Can` (`IsHidden`, a field's in camel case: `isMirrored`).
  - A contract is `I` plus a noun (`IMaterial`), and what it's handed is the noun plus `Inputs` (`SurfaceInputs`). A
    struct shared with C# ends in `Constants`, or starts with `Gpu` for a table's row. A heap reference ends in `Ref`,
    stage-to-stage data in `Varyings`, a draw's outputs in `Targets`. A set of codes is an `enum`.
- Put what a struct does on it as extensions (`instance.ToCameraRelative(position)`), so a struct shared with C# keeps
  only its fields. Return a struct rather than `out` parameters.
- A struct C# reads or writes holds only 32-bit scalars, their vectors, row-major matrices, heap references and such
  structs. Its C# twin is sequential with `Pack = 4` and names the Slang module in its summary. It can hold a heap
  reference as its handle (`GpuTexture`, `GpuSampler`). Nothing checks that the two match, so change them together.
- A contract between shaders is an interface (`IMaterial`, `ISky`, `IPixelEffect`), and a pass that works with any is
  generic over it.
- Totals across a dispatch run in a one-thread pass after it (`FitClusters`).
- Use wave intrinsics only where the result can't depend on wave size (32 or 64) or the order is already arbitrary. No
  16-bit types, and no runtime-indexed resource arrays but the heap's.
- Guard values where they go wrong: clamp roughness with the library's helper, keep n·v above 0, normalise with the
  helper that allows zero, and use `SampleGrad` or `SampleLevel` inside a branch.
- A compute pass has no implicit derivatives. Code it may run takes them as inputs, as `SurfaceInputs` and
  `SkyInputs` do.
- Few variants: a generic for what changes the pipeline, a `[branch]` on a draw-wide value for the rest.
- The material contract and the shading models live once in the shared shader library in `src/Resources/Shaders/`.
  Every renderer shades with them. A renderer's own tables and techniques, its BRDF included, go in its gem.
- Write a technique from its paper and name the paper in a comment. No third-party shader code.
