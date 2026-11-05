# AssimpModels

Reads FBX, glTF and OBJ files through assimp: provides `IAssetImporter<Model>`.

To use it, list `AssimpModels` in a project's `.magic` file.

- A file's nodes are flattened into one model in the engine's frame: left-handed, +Y up, a metre a unit, triangles
  clockwise from outside, with normals and tangents made where the file has none.
- One mesh per material slot, slots numbered as the file's materials are first used, up to the slots a renderer has.
- The `.meta` file's `origin` is the point moved to the model's (0, 0, 0).
- assimp is built from source by the native library kit with the three importers alone.
