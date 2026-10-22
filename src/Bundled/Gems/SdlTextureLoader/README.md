# SdlTextureLoader

Loads images with SDL_image. Provides `IAssetLoader<Texture>`.

Every image becomes RGBA8 pixels, with mipmaps unless its `.meta` turns them off. The `.meta` also says whether the
image is colour (sRGB) or data such as a normal map (linear) ([Textures](../../Resources/Textures/README.md)).
SDL_image comes from the SDL bindings project.
