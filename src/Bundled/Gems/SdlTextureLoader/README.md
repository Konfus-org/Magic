# SdlTextureLoader

Images to textures, through SDL_image ([ppy.SDL3_image-CS](https://github.com/ppy/SDL3-CS)): provides
`IAssetLoader<Texture>`. What a texture's `.meta` holds is in [Textures](../../Resources/Textures/README.md).

## Behaviour

- Whatever SDL_image decodes is converted to RGBA8, rows top first: sRGB for a `usage` of `color`, linear for `normal`
  and `mask`. An `.svg` is drawn at the `size` its `.meta` asks for.
- With `mipmaps`, each level is the one above halved with SDL's linear scaling, down to one texel, and every level is
  in one array, the first at its start.
- SDL_image needs no SDL started, so files load on any worker, several at once.

## Natives

ppy.SDL3_image-CS carries SDL_image, and the SDL it is built on, for every platform it supports, and the gem's load
context picks the machine's. SDL is called before SDL_image, so SDL_image binds to the SDL every gem shares
(`GemLoadContext`).
