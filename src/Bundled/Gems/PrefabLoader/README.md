# PrefabLoader

Loads `.prefab` files into prefabs ready to spawn. Provides `IAssetLoader<Prefab>`. The file format is in
[Prefabs](../../Resources/Prefabs/README.md). It has no settings.

A prefab is a tree of entities. Each entity lists its components by type name, its tags, its scripts and its children.
A script is named by its `.cs` asset's id, and its other keys set the script's public properties. A child can place
another prefab. The gem that runs scripts makes and starts them.

- A component, tag or script value that doesn't fit is logged and left out. The rest of the prefab loads.
- A placed prefab that doesn't load, or a prefab that places itself, fails the whole file.
- Editing a prefab reloads every prefab that places it.
