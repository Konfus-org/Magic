# Render textures

A `.rtex` file is a texture a camera draws into, which materials sample like any other (`RenderTexture`): it says its
size in texels, across and down, which is what the camera draws at. The keys and their defaults are the
`RenderTexture` type's properties, documented on it, and the files beside this README are the examples.

It has no image: it is black until a camera has drawn into it. A camera names one as the target it draws into, and a
material samples one as a render-texture param ([Materials](../Materials/README.md)).
