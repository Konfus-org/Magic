# SdlTextureLoader

Images to textures, through SDL_image, called through its generated API ([Gems](../README.md#native-libraries)):
provides `IAssetLoader<Texture>`. What a texture's `.meta` holds is in [Textures](../../Resources/Textures/README.md).
SDL_image and SDL come from the SDL bindings project, so SDL_image binds to the one SDL every gem shares
([Sharing a library](../README.md#sharing-a-library)).

## Behaviour

- Whatever SDL_image decodes is converted to RGBA8, rows top first: sRGB for a `usage` of `color`, linear for `normal`
  and `mask`. An `.svg` is drawn at the `size` its `.meta` asks for.
- With `mipmaps`, each level is the one above halved with SDL's linear scaling, down to one texel, and every level is
  in one array, the first at its start.
- SDL_image needs no SDL started, so files load on any worker, several at once.
