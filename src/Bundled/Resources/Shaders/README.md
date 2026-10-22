# Shaders

HLSL shared by every renderer: the conventions, the surface and sky contracts, the engine's surfaces and skies, and a
lighting library. A shader is an `.hlsl` or `.hlsli` asset with a `.meta`. A surface is a `.surf.hlsl` and a sky a
`.sky.hlsl` ([Materials](../Materials/README.md#writing-a-surface)). A gem can also keep shaders in its own
`Resources/Shaders/`. The rules every shader follows are in the [code standards](../../../../CODESTANDARDS.md#shaders).

## Writing a shader

Start with `Include/Bindings.hlsli`. Its header lists the conventions and the binding model.

- A `.pass` names its shader by id: a `.comp.hlsl` for a compute pass, a `.frag.hlsl` for a fullscreen pass, a
  `.vert.hlsl` for a depth draw. A draw of surfaces names none; the renderer builds one around each surface.
- A pass's shader reads the frame's textures and buffers by name. The renderer defines `HAS_Name` for each name the
  pass can see; guard a name the frame may not have with `#ifdef`.
- `#include` takes a path under `Shaders/`, such as `"Lighting/Lights.hlsli"`, from any root.
- Integer textures can't be sampled. Read them as storage textures.
- Draw what only the GPU can find wrong with `Include/Failure.hlsli`, and raise its code into the frame's failures
  buffer. The renderer shows each raised code as text. To add a code, number it in `Include/Failure.hlsli` and add
  its words in the renderer, in the same order.

## Lighting a pixel

`Lighting/Shade.hlsli` returns the light a surface sends toward the camera, in nits. It adds the key light through
its shadow, the other directional lights, the point and spot lights that reach the pixel, and light from everywhere
(the sky, global illumination and reflections), less occlusion. The renderer then multiplies by exposure. Each piece
has its own header under `Lighting/` or `Include/`.

- **Units:** directional lights in lux, point and spot lights in candela, the sky in nits. Emissive light is already
  pre-exposed.
- **Shading models:** metallic-roughness, clear coat, cloth and subsurface, picked per pixel by the surface
  (`Include/ShadingModel.hlsli`).
- **Specular anti-aliasing:** the renderer widens a surface's roughness by how far its normal turns across the pixel.
