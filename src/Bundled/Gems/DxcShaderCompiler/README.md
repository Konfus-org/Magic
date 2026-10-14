# DxcShaderCompiler

HLSL to SPIR-V, DXIL or Metal: provides `IShaderCompiler`, with Microsoft's DirectXShaderCompiler (DXC) called
directly, and SDL_shadercross for what DXC does not do. It needs the assets (`Assets`), which a shader's includes are,
and keeps what it compiles in the cache (`Caches`).

## Settings

In the project's `Presets/Default.preset`, by another preset, or `--set DxcShaderCompiler.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `debugInfo` | `false` | Embed each shader's source and symbols, so a GPU capture (RenderDoc, PIX, Xcode) steps through it. |

## Behaviour

- Every shader is HLSL 2021, entry point `main`, Shader Model 6.0 (`vs_6_0`, `ps_6_0`, `cs_6_0`), optimised (`-O3`),
  matrices column-major (`-Zpc`), and warnings are errors (`-WX`). `STAGE_VERTEX`, `STAGE_FRAGMENT` or
  `STAGE_COMPUTE` is defined in front of it, which `Shaders/Include/Bindings.hlsli` picks registers by.
- Each `#include "path"` line is replaced by the shader asset at `Shaders/path`, loaded through `Assets` from
  whichever root has it, so DXC reads nothing from disk. An include goes in where it is written, between `#line`s
  that keep DXC's errors naming its file and line, and its guards keep a second copy out. An include no asset is at
  is left for DXC to report; one that does not load fails the compile with the asset's error.
- SPIR-V first, whatever the format asked for: for Vulkan 1.1 (`-fspv-target-env=vulkan1.1`), so wave intrinsics
  become subgroup operations, with bindings kept where the source puts them. What a shader binds is counted from it
  (SDL_shadercross's reflection), which is what a GPU needs to make a pipeline.
- DXIL is compiled from the HLSL again, signed by DXC's `dxil` library, which the gem loads first so DXC finds it.
  Vulkan's attributes (`[[vk::image_format]]`) mean nothing to DXIL, so its compile alone does not warn of them.
- Metal is translated from the SPIR-V (SDL_shadercross, over SPIRV-Cross) to Metal Shading Language 2.3, which has the
  SIMD-group functions wave intrinsics become; the entry point is renamed `main0`.
- DXC runs on a worker, never the caller's thread. Each call makes its own DXC compiler, so the gem is safe from any
  thread.
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

## Natives

SDL3-CS's shadercross packages carry the natives for every platform, and the gem's load context picks the machine's.
SDL_shadercross links SDL, so the gem binds ppy.SDL3-CS too, and
SDL loads before it: the copy another gem loaded, shared by name, or the gem's own when it is made first.
