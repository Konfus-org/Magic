# Assets

`AssetManager` finds and loads what the files under the asset directories describe. A gem takes it in its
constructor.

A file is an asset when a `.meta` file is beside it, such as `Rock.png.meta` for `Rock.png`. The `.meta` is JSON: the
asset's `id`, a whole number above 0 that no other asset uses, its `type`, the name of its asset type such as
`"Material"`, and its import settings. Assets refer to each other by id, written `{ "id": 3400 }`, so a file can move
or be renamed. The asset directories are the project's `Assets/`, then each gem's `Resources/`, then Magic's built-in
`src/Resources/`; the first wins a path two of them have.

## Finding an asset

An asset is found by its path from its asset directory, such as `Materials/Checker.mat`, as a typed handle. A type's
default, `<Type>s/Default`, and the stand-in for a broken asset, `<Type>s/Failures/<BrokenType>`, whatever their
extension, come from the built-in assets only. Finding works from a gem's constructor on.
[`AssetManager.cs`](AssetManager.cs) has the calls.

## Loading an asset

`LoadAsync(handle, progress, cancel)` gives the asset or why it could not load. Every caller gets the same object. A
load asked for in a constructor starts with the first frame, once every gem is made. Cancelling stops only that caller's wait.

```csharp
Result<Curve> curve = await assets.LoadAsync(handle, progress, cancel).ConfigureAwait(false);
if (!curve.Ok)
    Log.Error(curve.Error);
```

`Add(handle, asset)` hands out an asset a gem made rather than read from a file.

Loads land between frames: the host pumps the asset manager once a frame, before the gems, so the assets hold still
for the whole of a frame.

## Dependencies

What an asset depends on is the handles it holds, found by reflection. A JSON or text asset is read for them without
loading anything; an asset a gem reads is loaded once to find them. `FindDependenciesAsync` and `FindDependentsAsync` crawl down to what an asset
depends on, or up to what depends on it, to a number of levels, `AssetManager.AllLevels` for all of them.
`LoadDependenciesAsync` and `LoadDependentsAsync` load what they find, each as the type its `.meta` names.

## Reloads

When a file or its `.meta` changes, the asset loads again, and so does every asset whose loader loaded it.
`GetVersion(handle)` changes with it: compare it with the version you loaded at, and load again when it moved. If the
new file fails to load, the error is logged and the old asset stays.

## Asset types

An asset type is a sealed class implementing `IAsset<T>` of itself, named by its class's name in a `.meta`'s
`"type"`. The types gems share are in `Types/`; a gem's own are found in its assembly.

- A property marked `[AssetMeta]` is an import setting, set by the asset's `.meta` under its name in camelCase, its
  default its initializer. A `.meta` key no marked property has is an error.
- A JSON file is read by Core when the type is marked `[AssetFormat(AssetFileFormat.Json)]`, in
  [the one dialect](../Serialization/Json.cs), whatever its extension.
- A text file is read by Core when the type is marked `[AssetFormat(AssetFileFormat.Text)]`, whatever its extension.
  The type has a `Text` string, set to the file's text.
- Any other format needs a gem that implements `IAssetLoader<T>`, which states the extensions it reads. One gem reads
  a type. The loader gets the file's id, bytes, metadata and version as an `AssetFile`, copies the metadata onto
  what it makes, and loads what the
  asset depends on with the `LoadAsync` that takes the file's id as the dependent.
