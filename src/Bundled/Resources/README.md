# Assets

How any asset is found, read and reloaded, wherever it lives: the engine's own, Core's and its bundled gems', in this
folder, which every project can use without shipping it; any other gem's, in its own `Resources/` folder; and a
project's, in its `Assets/` folder. All three work the same way. A kind of asset with authoring rules of its own has a
README beside its files, saying what its file holds and what must agree with the assets it names. What Core does with
every asset once it is loaded, how long it is kept, what its default and failure assets are and how a gem adds a type,
is in [Core's README](../../Core/README.md#assets).

## Finding an asset

**Roots.** Core indexes these folders as the gems are made, in this order, and watches them while it runs:

| Root | Where |
| --- | --- |
| The project's | `Assets/` beside its `.magic` file |
| The engine's | This folder, Core's assets and the bundled gems'; in a release, `Resources/` beside Magic, where the build copies it |
| Each gem the project lists | The gem's own `Resources/`, when it has one; in a release, `Gems/<Gem>/Resources/` beside Magic, where the build copies it |

Only a release copies the engine's and the gems' folders, which ships them with Magic; any other build reads them where
they are, so building stays fast and an edit here reaches a running engine at once
([the project file](../../Core/README.md#the-project-file)). A file is an asset when a `.meta` sidecar is beside it;
the READMEs are not copied. A path two roots have is the first's, so a project's file wins over the engine's, and a
gem's file may not share a path with the engine's or another gem's.

**Ids, not paths.** Every asset has a 64-bit id in the `.meta` beside it, and assets name each other by it:
`{ "id": 3363 }`. A file can be moved or renamed and everything that names it still finds it. Code that starts from a
name looks it up by its path relative to a root, with forward slashes: `assets.Find<Material>("Materials/Default.mat")`.

**The `.meta` sidecar** holds the asset's id, and the import settings its type's loader reads, as a texture's does:

```json
{ "id": 36, "wrap": "repeat", "filter": "linear", "mipmaps": true }
```

- `id`: a whole number above 0, unique across every root. How it is chosen does not matter, only that no other asset
  has it: a second file with an id already taken is refused with an error, unless it is at the same path in a later
  root, which the first overrides. A sidecar with no id, or that is not JSON, leaves the file unindexed, with an error.
- Whatever else its type's loader reads, camelCase (`size`, `origin`, `mipmaps`, …), explained in that type's README.
  A type Core reads as JSON or text has a sidecar holding only the id.

**Paths named in code.** A few assets beside the defaults are found by path, such as the loading domain when a project
names none: each must exist under some root, and is read from whichever root has it. A shader's `#include` names a
path too, `Shaders/<path>` ([Shaders](Shaders/README.md)).

## Reading an asset

**The C# type comes from whoever asks.** A `Handle<Material>` reads the file as a `Material`, with the loader for that
type: a file format, which turns the file's bytes and the import settings in its sidecar into the asset, what the
engine works with rather than the shape of the file. The extension only matters where the loader says so (an image's,
a model's). A type whose file is its class as JSON, or its text, is read by Core; any other is read by the loader a gem
provides for it, and is an error when the project lists no such gem ([Core's README](../../Core/README.md#assets)). A
loader may load other assets as it reads a file, as a prefab's loads the prefabs it places, and the file is read again
when one of those changes.

**The JSON** is Core's one dialect: camelCase keys matched exactly, enums by their names in snake_case, vectors as
`{ "x", "y", "z", "w" }`, colours as `{ "r", "g", "b", "a" }`, handles as `{ "id": N }`, comments and trailing commas
allowed, and a key the type does not have, or a null where it allows none, an error. JSON is how files are written,
never what the engine works with: an asset, a component or a script's value is the type it is by the time anything
past the loader sees it.

**Loading** is asynchronous (`Assets.LoadAsync`): the file is read on a worker, once however many ask, and every caller
gets the same object; what it names by handle is loaded by whoever needs it, and a failure is an error the caller
decides what to do with. A loader or post-processor that throws fails that file, and the failure is remembered until
the file or its sidecar changes, so a broken file fails once, not every frame. One asking while an asset is made again,
after a change, waits for what it is made into. A caller's cancellation token only stops its own wait.

**Post-processing.** Once its loader has made it, a gem's post-processor for the type, if the project lists one, works
on the asset before anyone gets it, on every load and reload: a model gets its clusters, a pipeline its passes. What is
slow to make that way is kept in [the cache](../../Core/README.md#the-cache), checked against the asset's version
(`Assets.GetVersion`), a hash of its file and its sidecar's settings, so it is made again only when either changes. What a
post-processor loads as it works is what the asset is made from (`Assets.GetDependencies`), and an asset made from itself,
a pipeline that brings itself in, directly or through others, fails that load rather than waiting for itself.

## Hot reload

Core watches every root where it reads it, and takes changes in once the files have been left alone for 300 ms (an
editor saving a file, or a build copying a folder, writes several times in a row): it indexes them again and compares
the new index with the old. A change to a `.meta` counts as a change to its asset, and every sidecar is read again
each time, so a broken one, or a duplicate id, is logged again after each change until it is fixed. What it tells, on
`Events`:

| Change | Message |
| --- | --- |
| A loaded asset's file or sidecar was written | `AssetReloaded<T>`, with the asset as it is now, even when nothing holds it any more, so whoever kept only what it made from it hears of it. One that no longer loads is an error, and what loaded before stays. |
| An asset something was made from was made again, or is gone | That is made again after it, then what was made from that, and so on out, each an `AssetReloaded<T>`: editing a pass makes again every pipeline it is in. |
| Files came, went, moved or were written | `AssetsChanged`, once what changed has loaded again. A moved file keeps its id. |
| The project's code, or Magic's scripts, were built | Core loads the code again ([the project file](../../Core/README.md#the-project-file)), then every script is made again as if its file were written, and every prefab after them, each an `AssetReloaded<T>`. |

Whatever holds an asset watches these and takes the new one in; what a gem does on a change is in its README.

## Adding an asset

**To the engine** (something every project should have, or a bundled gem needs): put the file here, in the folder
named for its type (`Shaders/`, `Materials/`, …), write its `.meta` with an id of its own, and read that folder's
README for what it must agree with. This folder holds Core's assets and every bundled gem's, so a gem that ships with
Magic has no `Resources/` of its own; a script here is compiled by this folder's project, as a game's are by its own.
Any other gem keeps its files in its own `Resources/`, a root of its own.

**To a project**: put the file anywhere under the project's `Assets/`, with its `.meta` beside it. Which domain the
engine opens first, which it shows while another loads and which prefab is the player are the `.magic` file's to say,
and a game's code is the scripts among its assets and the components they use
([the project file](../../Core/README.md#the-project-file)); what a game needs from a gem, such as a file format, is a
gem the project lists.

**From a gem**: a gem adds asset *types*, not files. A type is declared in code, with a default like every type, and a
format a gem reads is a loader it provides ([Core's README](../../Core/README.md#assets)). The files a gem needs itself,
such as a renderer's shaders or its type's default, are in this folder for a gem that ships with Magic, and in the
gem's own `Resources/` for any other.
