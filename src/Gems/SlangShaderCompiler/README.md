# SlangShaderCompiler

Compiles Slang entries to SPIR-V for the GPU provider: provides `IShaderCompiler`.

To use it, list `SlangShaderCompiler` in a project's `.magic` file.

- A shader is a `.slang` asset under `Shaders/`; `import A.B;` is the asset at `Shaders/A/B.slang`. An entry's
  module and everything it imports load into one compile, so a change to any of them is a new compile.
- What compiles is cached under a hash of every module's text, in the project's cache, so the second run of an
  unchanged shader compiles nothing. A change to the compiler's options or tag is a new cache version, which
  compiles everything once more.
- Warnings are errors. Matrices are row-major. Entry points keep their names.
- The compiler is built from source by the native library kit; its API is C++ interfaces, which the bindings
  generator cannot bind, so `Slang.cs` binds by hand the few calls the gem makes, by the indices `slang.h` gives them
  at the tag the project builds.
