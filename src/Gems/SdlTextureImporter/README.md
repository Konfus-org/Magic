# SdlTextureImporter

Reads PNG, JPEG, TGA, BMP, SVG and ICO files into textures ready to upload, through SDL_image, and keeps the result in the
cache under the file's version: provides `IAssetImporter<Texture>`. Writes a plain RGBA8 texture as a PNG, through SDL:
provides `IAssetExporter<Texture>`.

To use it, list `SdlTextureImporter` in a project's `.magic` file.

- A file decodes, gets its mip chain when its `.meta` asks for one, and every level is packed into BC7 blocks, sRGB for
  colour and linear for a normal map or a mask, as the `.meta`'s usage says.
- A `.meta` with `isCompressed` off keeps plain RGBA8, for an image the CPU reads. An `.icon` file's image needs it, as a window's icon is plain pixels.
- An `.svg` is drawn at the width its `.meta`'s `size` says.
