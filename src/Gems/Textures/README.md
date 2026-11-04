# Textures

Reads PNG, JPEG, TGA and BMP files into textures ready to upload, and keeps the encoded result in the cache under the
file's version: provides `IAssetLoader<Texture>`.

To use it, list `Textures` in a project's `.magic` file.

- A file decodes, gets its mip chain when its `.meta` asks for one, and every level is packed into BC7 blocks, sRGB
  for colour and linear for a normal map or a mask, as the `.meta`'s usage says.
- The decoder and the block encoder come from NuGet; nothing native is built.
