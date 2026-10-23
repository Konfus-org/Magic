# Shaders

HLSL shared by every renderer: the conventions, the surface and sky contracts, the engine's surfaces and skies, and a
lighting library. A shader is an `.hlsl` or `.hlsli` asset with a `.meta`. A surface is a `.surf.hlsl` and a sky a
`.sky.hlsl` ([Materials](../Materials/README.md#writing-a-surface)). A gem can also keep shaders in its own
`Resources/Shaders/`. The rules every shader follows are in the [code standards](../../../../CODESTANDARDS.md#shaders).

## Writing a shader

Start with `Include/Bindings.hlsli`, whose header lists the conventions, and `Include/Heap.hlsli`.

- A shader reads every buffer, texture and sampler through the heap, by the ids in its one constant block
  (`CONSTANTS`), at most 128 bytes. One that declares a resource anywhere else doesn't compile.
- A pipeline script runs a compute shader with `IRendering.Dispatch`, and a post effect runs its shader with
  `IRendering.Effect` ([Rendering](../../../Core/README.md#rendering)). A draw of surfaces names no shader; the
  renderer builds one around each surface.
- `#include` takes a path under `Shaders/`, such as `"Lighting/Lights.hlsli"`, from any root.
- Integer textures can't be sampled. Read them texel by texel with `UINT_TEXTURE`.
- Draw what only the GPU can find wrong with `Include/Failure.hlsli`, and raise its code into the frame's failures
  buffer. The renderer shows each raised code as text. To add a code, number it in `Include/Failure.hlsli` and add
  its words in the renderer, in the same order.

## Lighting a pixel

The headers under `Lighting/` and `Include/` work out the light a surface sends toward the camera, in nits: from the
directional, point and spot lights, and from the sky all around. The renderer then multiplies by exposure.

- **Units:** directional lights in lux, point and spot lights in candela, the sky in nits. Emissive light is already
  pre-exposed.
- **Shading models:** metallic-roughness, clear coat, cloth and subsurface, picked per pixel by the surface
  (`Include/ShadingModel.hlsli`).
- **Specular anti-aliasing:** the renderer widens a surface's roughness by how far its normal turns across the pixel.
