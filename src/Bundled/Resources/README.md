# Assets

An asset is a file the engine loads, such as a model, a texture, a material or a prefab. This folder holds the
engine's built-in assets, which every project can use, in a folder per type. Most of those folders have a README on
how to make that kind of asset. What Core does with an asset once it is loaded is in
[Core's README](../../Core/README.md#assets).

## Finding an asset

Assets live under three roots, searched in this order: the project's `Assets/` folder, this folder, and each gem's
`Resources/` folder. When two roots have a file at the same path, the earlier root's file is found.

A file is an asset when it has a `.meta` file next to it, such as `Rock.png.meta` for `Rock.png`. The `.meta` is JSON.
It holds the asset's `id`, a whole number above 0 that no other asset uses, and any import settings its loader reads.

Assets refer to each other by id, written `{ "id": 3400 }`. You can move or rename a file freely. Code can also find
an asset by its path from its root, with forward slashes, such as `Materials/Checker.mat`.

Every asset type has a default in this folder, named `Default` in the type's folder, such as `Materials/Default.mat`.

## Reading an asset

Core reads types whose file is plain JSON or text, such as materials and domains. Other formats, such as images and
models, need a gem that loads them, listed in the project. Every JSON file follows the same rules:

- Keys are camelCase and must match exactly. An unknown key, or a null where the type allows none, is an error.
- Enums are names in snake_case, such as `"masked"`.
- Vectors and colours are objects with a key per component: `{ "x": 1, "y": 0, "z": 0 }`, `{ "r": 1, "g": 1, "b": 1 }`.
- Comments and trailing commas are allowed.

Slow work done after loading, such as a model's clusters, is kept in [the cache](../../Core/README.md#the-cache) and
done again only when the file or its `.meta` changes.

## Hot reload

Core watches every root while Magic runs. When you save a file or its `.meta`, the asset reloads, and so does
everything made from it: editing a shader reloads every pipeline that uses it. If the new file fails to load, the error
is logged and the old asset stays in use. Rebuilding the project's code reloads its scripts and prefabs. Gems hear
about each change on `Events`.
