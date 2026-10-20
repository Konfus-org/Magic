# DxcShaderCompiler

HLSL to SPIR-V, DXIL or Metal: provides `IShaderCompiler`, with Microsoft's DirectXShaderCompiler (DXC) and
Khronos's SPIRV-Cross, both built here from their repositories ([Gems](../README.md#native-libraries)), DXC's library
alone (an LLVM, the one build that takes long). SPIRV-Cross is called through its generated API. DXC is a COM API,
which the generator cannot express, so it is the one library bound by hand, in the gem, and the gem drives it directly:
a front end that fixes DXC's SPIR-V target at Vulkan 1.0 could not compile these shaders, whose wave intrinsics need
1.1. The gem needs the assets (`Assets`), which a shader's includes are, and keeps what it compiles in the cache
(`Caches`).

## Settings

Its one setting is documented on `DxcShaderCompilerSettings` and shown in the settings window. `debugInfo` embeds
each shader's source and symbols, so a GPU capture steps through it; it is part of what names a cache entry, so
changing it while running compiles every shader again.

## Behaviour

- Every shader is HLSL 2021, entry point `main`, Shader Model 6.0 (`vs_6_0`, `ps_6_0`, `cs_6_0`), optimised (`-O3`),
  matrices column-major (`-Zpc`), and warnings are errors (`-WX`). `STAGE_VERTEX`, `STAGE_FRAGMENT` or
  `STAGE_COMPUTE` is defined in front of it, which `Shaders/Include/Bindings.hlsli` picks registers by.
- Each `#include "path"` line is replaced by the shader asset at `Shaders/path`, loaded through `Assets` from
  whichever root has it, so DXC reads nothing from disk. An include goes in where it is written, between `#line`s
  that keep DXC's errors naming its file and line, and its guards keep a second copy out. An include no asset is at
  is left for DXC to report; one that does not load fails the compile with the asset's error.
- SPIR-V first, whatever the format asked for: for Vulkan 1.1 (`-fspv-target-env=vulkan1.1`), so wave intrinsics
  become subgroup operations. What a shader binds is read from the SPIR-V by name (`SpirvBindings`): each resource it
  uses, what it is and the register it is at; a resource the shader does not use is dropped by the compiler and so
  not listed. The counts of each kind are what a GPU needs to make a pipeline; the names are what a renderer binds by.
- DXIL is compiled from the HLSL again and signed by the gem (`DxilHash`): the validator's hash, Microsoft's published
  algorithm, written into the container, which is all Direct3D 12 checks a shader for.
  Vulkan's attributes (`[[vk::image_format]]`) mean nothing to DXIL, so its compile alone does not warn of them.
- Metal is translated from the SPIR-V by SPIRV-Cross (`Metal`) to Metal Shading Language 2.3, which has the SIMD-group
  functions wave intrinsics become. Metal has no descriptor sets, so each resource gets the texture, sampler or buffer
  index the GPU gem's Metal backend expects of its set and binding, and the entry point is renamed `main0`.
- DXC runs on a worker, never the caller's thread. Each call makes its own DXC compiler, so the gem is safe from any
  thread. A shader asked for while it already compiles, as when the passes are built again before the last build is
  done, waits for that compile rather than starting its own, so each compiles, and its cache entry is written, once.
- A failure is a `Result` carrying DXC's own text, file and line, so it reads like any compiler's.

## Cache

What compiles is kept in the cache, so a shader compiles once rather than on every run; the caller keeps the shaders it
uses. The gem provides `ICache<GpuShader>`, which writes a compiled shader's bytecode and binding counts as they are.

- An entry is named by a hash of the source with its includes spliced in, and the shader's name, stage and format,
  so every source has its own: the variants of a shader a renderer compiles under one name keep an entry each. Editing
  the shader, or any shader it includes, names another entry, so it compiles again; the old entry stays until `Cache/`
  is deleted.
- It is made with `debugInfo`, so changing it finds the entries dirty, and they compile again.
- DXC's arguments are the gem's own, so changing them raises the cache's version, which makes every entry dirty.
