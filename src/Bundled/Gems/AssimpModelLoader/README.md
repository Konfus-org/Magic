# AssimpModelLoader

Loads FBX, glTF and OBJ models with [Assimp](https://github.com/assimp/assimp). Provides `IAssetLoader<Model>`.

Models are converted to the engine's units and axes: metres, +Y up and the front at +Z. A model keeps the size it had
in the tool it was made in, whatever the export's scale and axes. A model's `.meta` sets its origin
([Models](../../Resources/Models/README.md)). Assimp is a native library
([Native libraries](../README.md#native-libraries)).

- Export with normals. A mesh without them fails to load.
- In an FBX file, a mesh's material slots follow the order its faces first use them. To keep a Blender model's slots,
  export it with its faces in slot order.
- Meshes made of points or lines are skipped.
