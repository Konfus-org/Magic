# Textures

Images: anything SDL_image reads (`.png`, `.jpg`, `.bmp`, `.tga`, `.webp`, `.svg`, …) is a `Texture`, decoded by the
SDLImage gem into pixels ready to upload, its mips made, so the renderer only copies bytes. A `.rtex` is a render
texture: a texture a camera draws into.

| File | Id | Used by |
| --- | --- | --- |
| `Checkerboard.png` | 36 | `Materials/Checker.mat` |
| `Question.png` | 3 | nothing in Resources yet |
| `SunnySky.png`, `DarkSky.png`, `BoxIcon.png`, `ToyboxIcon.png` | random | nothing in Resources yet |

## The `.meta`

```json
{ "id": 36, "version": 1, "usage": "color", "mipmaps": true }
```

| Key | Default | What it does |
| --- | --- | --- |
| `usage` | `color` | `color` and `ui` are sRGB; `normal` and `mask` are linear. `environment` is reserved. |
| `mipmaps` | true | Make the mip chain. Off for an image only ever shown at its size. |
| `size` | 0 | For an `.svg`: the pixel size to rasterise at (0 is the size the file states). |
| `wrap` | `repeat` | `repeat`, `clamp_to_edge`, `mirrored_repeat`. **Not honoured yet**: material textures share one sampler. |
| `filter` | `linear` | `linear`, `nearest`. **Not honoured yet**, for the same reason. |
| `channels` | `rgba` | `rgba`, `rgb`. **Not honoured yet**. |

There is no block compression: textures are uploaded as RGBA8.

## How the renderer uses it

A material names a texture in a `TextureRef` param (`"colorMap": { "texture": { "id": 36 } }`). Material textures
live in eight GPU texture arrays, one per size (256, 512, 1024, 2048) and colour space (sRGB, linear), all bound at
once, so a material picks a layer and every material of a surface still draws in one call.

- A texture is fitted to the smallest array that holds it; anything larger than 2048 is shrunk to 2048.
- An array holds at most 256 textures. One that arrives at a full array draws as a failure, and the log says so.
- A texture that does not load draws its materials with the failure surface.

## Lifetime

Loaded when the first material that samples it is packed, counted per material, and its layer freed when the last
goes. Pooled in memory under the `Texture` budget (512 MB; `"Assets.Budgets": { "Texture": … }`). Editing the image
or its `.meta` uploads it again and repacks the materials that use it.

## Render textures (`.rtex`)

```json
{ "width": 512, "height": 512 }
```

A camera whose `Camera` component targets it draws into it instead of a window, and any material can sample it like a
texture: see the RenderTexture sample (`Samples/Assets/Domains/RenderTexture`).

## Adding a texture

Drop the image under `Assets/` (or `Resources/Textures/` for the engine), set `usage` in its `.meta` (`normal` for a
normal map, `mask` for roughness/metallic/occlusion data), and name its id from a material.
