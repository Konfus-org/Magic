# Textures

A `.png`, `.jpg`, `.jpeg`, `.bmp`, `.tga`, `.webp` or `.svg` file is a `Texture`. The image loader decodes it and makes
its mips when it loads.

The `.meta` can hold import settings, such as whether the image is colour, a normal map or a data mask like
roughness. The image loader's import settings type documents them, and `Default.png.meta` is an example. Say what
the image is for when it is not colour.

Use a texture from a material's texture param ([Materials](../Materials/README.md)). A `.rtex` is a
[render texture](../RenderTextures/README.md), not an image.
