# Materials

A material says what a surface looks like. A `.mat` file names a shader and gives values to its params. The
`Material` type documents the format, and the files here are examples.

- The shader is a surface for meshes, or a sky for the world's sky.
- A material is opaque, masked (pixels below the surface's alpha cutoff are cut out) or transparent.
- Params are named after the fields of the shader's struct, the one named like its file. One left out takes the
  field's initializer. The `MaterialParam` type documents how each kind of value is written.
- Code can change a material's params while it is drawn. The change lasts until the file changes.

Use a material from an entity's `Renderer`, in the slot its model's mesh names ([Models](../Models/README.md)), or
from the world's `Sky` component.

## Writing a surface

A surface is a module whose struct, named like its file, implements `ISurface` from
[Shaders/Core/Surface.slang](../Shaders/Core/Surface.slang). Its fields are its params, each with its default as its
initializer, and its `Evaluate` says what the surface is made of. The module documents the inputs, the outputs and the
helpers for textures and normal maps. The renderer does the lighting ([Shaders](../Shaders/README.md#lighting-a-pixel)).
The surfaces in `Shaders/Surfaces/` are examples.

A param that says what light the surface passes on to the world carries its role: `[GiColor]` for its colour,
`[GiColorMap]` for the texture that colour is read from, `[GiMetallic]` and `[GiEmissive]`. The renderer lights the
world with them without running the surface. A transparent surface's `[GiColor]` is the tint of the light crossing it.

## Writing a sky

A sky is a module whose struct, named like its file, implements `ISky` from
[Shaders/Core/Sky.slang](../Shaders/Core/Sky.slang), its fields its params as a surface's are. Its light is in nits.
The procedural sky in `Shaders/Skies/` is the example.
