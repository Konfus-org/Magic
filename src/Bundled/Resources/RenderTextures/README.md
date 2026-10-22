# Render textures

A `.rtex` file is a texture a camera draws into. It sets its size in texels, documented on the `RenderTexture` type,
and `Default.rtex` is the example. It is black until a camera draws into it.

Set it as a camera's target, and sample it from a material's texture param ([Materials](../Materials/README.md)).
