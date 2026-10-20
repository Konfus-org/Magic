# Textures

Images: a `.png`, `.jpg`/`.jpeg`, `.bmp`, `.tga`, `.webp` or `.svg` file is a `Texture`, decoded into RGBA8 pixels
ready to upload, its mips made, so whatever draws it only copies bytes. The image loader reads it, with the import
settings in its `.meta` ([its README](../../Gems/SdlTextureLoader/README.md)). A `.rtex` is not a `Texture` but a
[render texture](../RenderTextures/README.md): a texture a camera draws into.

## The `.meta`

Beside the id, the sidecar holds the image's import settings: what the image is for (colour, which is sRGB, or a
normal map or a mask of data such as roughness, metalness or occlusion, which are linear), whether its mip chain is
made as it loads, each level half the one above, which an image only ever shown at its size can do without, the
width an `.svg` is drawn at, with its height in proportion, what is read past its edge (repeat, clamp or mirror) and
how it is read between texels (linear or nearest). The keys and their defaults are the image loader's import-settings
type's properties, documented on it, and the sidecars beside the images in this folder are the examples.

## Adding a texture

Drop the image under the project's `Assets/`, or in this folder for the engine, say in its `.meta` what it is for if
it is not colour, and name it from a material's texture param ([Materials](../Materials/README.md)).
