# DxcShaderCompiler

HLSL to SPIR-V, DXIL or Metal: provides `IShaderCompiler`, with Microsoft's DirectXShaderCompiler (DXC) and
Khronos's SPIRV-Cross ([Gems](../README.md#native-libraries)), DXC's library alone: Microsoft's own build where it
publishes one, since the library is an LLVM, and built here where it does not. SPIRV-Cross is called through its
generated API. DXC is a COM API, which the generator cannot express, so it is the one library bound by hand, in the
gem, and the gem drives it directly:
a front end that fixes DXC's SPIR-V target at Vulkan 1.0 could not compile these shaders, whose wave intrinsics need
1.1. The gem needs the assets (`Assets`), which a shader's includes are, and keeps what it compiles in the cache
(`Caches`).

## Settings

Its one setting is documented on `DxcShaderCompilerSettings` and shown in the settings window. Debug information
embeds each shader's source and symbols, so a GPU capture steps through it; it is part of what names a cache entry, so
changing it while running compiles every shader again.

## Behaviour

- Every shader is HLSL 2021, entry point `main`, Shader Model 6.0, optimised, matrices column-major, and warnings
  are errors. A macro saying which stage is compiled is defined in front of it, which the shader library's binding
  header picks registers by.
- Each `#include "path"` line is replaced by the shader asset at `Shaders/path`, loaded through `Assets` from
  whichever root has it, so DXC reads nothing from disk. An include goes in where it is written, between `#line`s
  that keep DXC's errors naming its file and line, and its guards keep a second copy out. An include no asset is at
  is left for DXC to report; one that does not load fails the compile with the asset's error.
- SPIR-V first, whatever the format asked for: for Vulkan 1.1, so wave intrinsics become subgroup operations. What a
  shader binds is read from the SPIR-V by name: each resource it uses, what it is and the register it is at; a
  resource the shader does not use is dropped by the compiler and so not listed. The counts of each kind are what a
  GPU needs to make a pipeline; the names are what a renderer binds by.
- DXIL is compiled from the HLSL again, validated and signed by DXC's own validator, whose hash in the container is
  all Direct3D 12 checks a shader for. Vulkan's attributes mean nothing to DXIL, so its compile alone does not warn of
  them.
- Metal is translated from the SPIR-V by SPIRV-Cross to the Metal Shading Language version that has the SIMD-group
  functions wave intrinsics become. Metal has no descriptor sets, so each resource gets the texture, sampler or buffer
  index the GPU gem's Metal backend expects of its set and binding, and the entry point is renamed as Metal needs.
- DXC runs on a worker, never the caller's thread. Each call makes its own DXC compiler, so the gem is safe from any
  thread. A shader asked for while it already compiles, as when the passes are built again before the last build is
  done, waits for that compile rather than starting its own, so each compiles, and its cache entry is written, once.
- A failure is a `Result` carrying DXC's own text, file and line, so it reads like any compiler's.

## Cache

What compiles is kept in the cache, so a shader compiles once rather than on every run; the caller keeps the shaders it
uses. The gem provides the `ICache<T>` for a compiled shader, which writes its bytecode and binding counts as they are.

- An entry is named by a hash of the source with its includes spliced in, and the shader's name, stage and format,
  so every source has its own: the variants of a shader a renderer compiles under one name keep an entry each. Editing
  the shader, or any shader it includes, names another entry, so it compiles again; the old entry stays until the
  cache's folder is deleted, and another checkout that still has the old source still reads it.
- It is made with the debug-information setting, so changing it finds the entries dirty, and they compile again.
- DXC's arguments are the gem's own, so changing them raises the cache's version, which makes every entry dirty.
