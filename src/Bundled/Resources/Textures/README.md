# Textures

Images: a `.png`, `.jpg`/`.jpeg`, `.bmp`, `.tga`, `.webp` or `.svg` file is a `Texture`, decoded into RGBA8 pixels
ready to upload, its mips made, so whatever draws it only copies bytes. The image loader gem reads it, with the import
settings in its `.meta`. A `.rtex` is not a `Texture` but a [render texture](../RenderTextures/README.md): a texture a
camera draws into.

## The `.meta`

```json
{ "id": 36, "usage": "normal", "wrap": "clamp" }
```

| Key | Default | What it does |
| --- | --- | --- |
| `usage` | `color` | `color` is sRGB; `normal` (a normal map) and `mask` (data such as roughness, metalness or occlusion) are linear. |
| `mipmaps` | `true` | Make the mip chain, each level half the one above. Off for an image only ever shown at its size. |
| `size` | `0` | For an `.svg`: the width in pixels it is drawn at, its height in proportion; 0 is the size the file states. |
| `wrap` | `repeat` | What is read past its edge: `repeat`, `clamp` or `mirror`. |
| `filter` | `linear` | How it is read between texels: `linear` or `nearest`. |

## Levels of detail

A texture's levels of detail are its mips, which its loader makes anyway: nothing more is made or cached for them. A
`Renderer` at `lod` n reads its textures from mip n down, so a texture with no mips has no levels of detail.

## Adding a texture

Drop the image under `Assets/` (or Core's `Resources/Textures/` for the engine), set its `usage` if it is not colour,
and name its id from a material's `TextureRef` param: `"colorMap": { "texture": { "id": 36 } }`.
