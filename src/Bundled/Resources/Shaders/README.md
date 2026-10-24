# Shaders

The shader library every renderer shares, in [Slang](https://shader-slang.org): the modules every shader imports, the
contracts a renderer is extended by, the engine's surfaces, skies and post effects, and the lighting. A shader is a
`.slang` asset with a `.meta`, and is one module. A gem can also keep shaders in its own `Resources/Shaders/`. The rules
every shader follows are in the [code standards](../../../../CODESTANDARDS.md#shaders).

## Modules

A module is named by its path under `Shaders/`: `Core/Heap.slang` is `Core.Heap`, which another shader imports with
`import Core.Heap;` from any root. Each starts with its own name, `module "Core/Heap";`, and marks what others use
`public`.

- `Core/` is what any shader may need: the heap, the pass header and the frame's clocks, maths, colour, noise, compute
  helpers, failures, and the contracts below.
- `Lighting/` works out the light a surface sends toward the camera ([Lighting a pixel](#lighting-a-pixel)).
- `Surfaces/`, `Skies/` and `Post/` are the engine's own. `Default.slang` is the default shader, the PBR surface.

## Extending

Each kind of thing a renderer draws with is a contract in the library. To add one:

| To add | Write |
| --- | --- |
| A surface | A module whose struct, named like its file, implements `ISurface` (`Core/Surface`). Its fields are the params a material gives values for, each with its default as its initializer. See [Materials](../Materials/README.md#writing-a-surface). |
| A sky | The same, implementing `ISky` (`Core/Sky`). |
| A kind of light | A struct implementing `ILight` (`Lighting/Lights`), a table of them in `WorldLights` and its C# twin, and a loop over them in `ShadeLights`. |
| A kind of shadow | A struct implementing `IShadowing` (`Lighting/Lights`), which the lighting pass hands `ShadeLights`. |
| A weather value | A field in `WeatherConstants` (`Core/Weather`) and its C# twin, which the renderer fills from its weather effect. |
| A frame value | A field in `FrameConstants` (`Core/Pass`) and the renderer's twin. `pass.clocks` reads it in any pass. |
| A post effect | For one that changes each pixel by itself, a module whose struct, named like its file, implements `IPixelEffect` (`Core/Post`), its fields its values, and a C# `IPixelEffect` that names its shader and writes its values. For one that reads its neighbours, a compute pass taking `PostImages` and its values, run with `IRendering.Effect`. |
| An overlay | An `IOverlayScript` that draws with `Overlay/Overlay`'s entry points, or with its own that encode for the target with `EncodeForTarget` (`Core/Color`). |

A renderer specializes its passes with a surface's, a sky's or a run of pixel effects' struct, so the compiler checks
each against its contract.

## Writing a pass

- A pass is an entry point, `[shader("compute")]`, `[shader("vertex")]` or `[shader("fragment")]`, named for its job,
  such as `ShadeLighting`. One module holds a feature's passes.
- Its constants are its uniform parameters, at most 128 bytes: the pass header (`PassHeader`) first, then its own
  struct. The C# side pushes the same fields in the same order, as a sequential C# struct packs them, and marks that
  struct `[ShaderStruct]`.
- The header names the frame's constants and the view's. `pass.clocks` reads the frame's clocks in any renderer. A
  renderer's own passes load the rest themselves.
- Every buffer, texture and sampler is a reference into the heap: `ArrayRef<T>` for rows of a struct, `BufferRef` for
  bytes, `TextureRef`, `UintTextureRef`, `CubeRef` and their levels for textures, and `SamplerRef`. A shader that
  declares a resource any other way doesn't compile. Integer textures can't be sampled: `UintTextureRef.Load` reads
  them texel by texel.
- Work that adds up what every group of a dispatch did runs in its last group (`IsLastGroup` in `Core/Compute`), not
  in a pass of its own.
- A pipeline script runs a compute pass with `IRendering.Dispatch` ([Rendering](../../../Core/README.md#rendering)).
- Draw what only the GPU can find wrong as `Core/Failure` does, and raise its code with `RaiseFailure`. The renderer
  shows each raised code as text. To add a code, add it to `FailureCode` and its words to the renderer, in the same
  order.

## Lighting a pixel

The modules under `Lighting/` work out the light a surface sends toward the camera, in nits: from the world's lights
(`Lights`), and from the sky all around (`SkyLight`). The renderer then multiplies by exposure.

- **Units:** directional lights in lux, the sky in nits. Emissive light is already pre-exposed.
- **Shading models:** metallic-roughness, clear coat, cloth and subsurface, picked per pixel by the surface's
  `ShadingModel`.
- **Specular anti-aliasing:** the renderer widens a surface's roughness by how far its normal turns across the pixel.
