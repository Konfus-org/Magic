# Models

A `.fbx`, `.gltf`, `.glb` or `.obj` file is a `Model`: one or more meshes. The model loader converts it to the
engine's units and axes: metres, +Y up, +X right and the front at +Z.

- Export with normals. A mesh without them is refused.
- Export as one file. A `.gltf` with separate buffer files does not load; use a `.glb`, or embed the buffers.
- Each mesh is drawn with the material in the slot it names, 0 to 7: its material's index in the file.
- The `.meta` can set the model's origin, the point that sits at the entity's position. The model loader's import
  settings type documents it, and the `.meta` files here are examples.

To draw a model, name it and its materials in an entity's `Renderer` component.

When a model loads, a gem that makes clusters builds its clusters and its cards, flat pictures drawn far away. They
are kept in the cache and made again only when the file or its `.meta` changes.
