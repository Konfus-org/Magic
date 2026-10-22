# Materials

A material says what a surface looks like. A `.mat` file names a shader and gives values to its params. The
`Material` type documents the format, and the files here are examples.

- The shader is a surface (`.surf.hlsl`) for meshes, or a sky (`.sky.hlsl`) for the world's sky.
- A material is opaque, masked (pixels below the surface's alpha cutoff are cut out) or transparent.
- Params are named after the members of the shader's `MaterialParams` struct. One left out takes the shader's
  default. The `MaterialParam` type documents how each kind of value is written.
- Code can change a material's params while it is drawn. The change lasts until the file changes.

Use a material from an entity's `Renderer`, in the slot its model's mesh names ([Models](../Models/README.md)), or
from the world's `Sky` component.

## Writing a surface

A surface shader includes [Shaders/Include/Surface.hlsli](../Shaders/Include/Surface.hlsli). It declares a
`MaterialParams` struct and one function that says what the surface is made of. The header documents the inputs, the
outputs and the helpers for textures and normal maps. The renderer does the lighting
([Shaders](../Shaders/README.md#lighting-a-pixel)). The surfaces in `Shaders/Surfaces/` are examples.

## Writing a sky

A sky shader includes [Shaders/Include/Sky.hlsli](../Shaders/Include/Sky.hlsli). It declares a `MaterialParams`
struct and the functions the header lists. Its light is in nits. The procedural sky in `Shaders/Skies/` is the example.
