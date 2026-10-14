# Assets

How any asset is found, read, kept and reloaded: the engine's own, Core's and its bundled gems', in this folder (what
every project can use without shipping it), any other gem's, in its own `Resources/` folder, and a project's, in its
`Assets/` folder, which all work exactly the same way. A kind of asset with rules of its own has a README beside its files: how it works, and what
must agree with the assets it names.

## Finding an asset

**Roots.** Core indexes these folders as the gems are made, in this order, and watches them while it runs:

| Root | Where |
| --- | --- |
| The project's | `Assets/` beside its `.magic` file |
| The engine's | This folder, Core's and the bundled gems' assets; in a release, `Resources/` beside Magic, where the build copies it |
| Each gem the project lists | The gem's own `Resources/`, when it has one (a bundled gem's are here); in a release, `Gems/<Gem>/Resources/` beside Magic, where the build copies it |

Only a release copies the engine's and the gems' folders, which ships them with Magic; any other build reads them where
they are, so building stays fast (`Project.Resources`, in [Core's README](../../Core/README.md#the-project-file)). A
file is an asset when a `.meta` sidecar is beside it; the READMEs are not copied. A path two roots have is the first's,
so a project's file wins over the engine's.

**Ids, not paths.** Every asset has a 64-bit id in the `.meta` beside it, and assets name each other by it:
`{ "id": 10030 }`. A file can be moved or renamed and everything that names it still finds it. Code that starts from
a name looks it up by its path relative to a root, with forward slashes:
`assets.Find<Material>("Materials/Default.mat")`.

**The `.meta` sidecar** holds the asset's id, and the import settings its type's loader reads:

```json
{ "id": 520, "origin": { "x": -1, "y": 0, "z": 0 } }
```

- `id`: unique across every root, a whole number above 0. A second file with an id already taken is refused with an
  error, unless it is at the same path in a later root, which the first overrides; a sidecar with no id, or that is
  not JSON, leaves the file unindexed, with an error.
- Whatever else its type's loader reads, camelCase: `size`, `origin`, `mipmaps`, …; a JSON type's sidecar holds only
  the id.
- A file without a sidecar is no asset: write one, with an id no other asset has. The engine's own assets take small,
  readable ids; a project's take random ones, which never collide in practice.

**Default assets.** Every asset type has a default: the file named `Default`, with one of the type's extensions, in the
folder named for the type, `<Type>s/Default.<ext>` (or `<Type>s/Default/Default.<ext>` for an asset that is a folder,
like a domain), found by `Assets.Default<T>()`. Code names no path for it, and a project replaces one by having its
own at the same path, since the project's file comes first.

**Paths named in code.** A few assets beside the defaults are found by path, such as the loading domain when a project
names none: each must exist under some root, and is read from whichever root has it. A shader's `#include` names a
path too, `Shaders/<path>`.

## Reading an asset

**The C# type comes from whoever asks.** A `Handle<Material>` reads the file as a `Material`, with the
`IAssetLoader<T>` for the type: a type's file format. It turns the file's bytes and the import settings in its sidecar,
read as a settings type of the loader's own, into the asset, what the engine works with rather than the shape of the
file; the extension only matters where the loader says so (an image's, a model's). A type says how its file is read by
what it implements: an `IJsonAsset<T>` is its class as JSON, and an `ITextAsset<T>` is the file's text, both read by
Core when no gem provides a loader for the type, so the class is all there is to write; any other `IAsset<T>` is read by
the loader a gem provides for it, and is an error when the project lists no such gem. A loader may load other assets as it reads a file, as a prefab's
loads the prefabs it places, and the file is read again when one of those changes.

**Where JSON stops.** JSON is how files are written, never what the engine works with: it is read by the loaders and by
whatever reads the project's files, and an asset, a component or a script's value is the type it is by the time anything
else sees it.

**The JSON** is Core's one dialect ([Core's README](../../Core/README.md#json)): camelCase keys matched exactly,
vectors as `{ "x", "y", "z", "w" }`, colours as `{ "r", "g", "b", "a" }`, handles as `{ "id": N }`, comments and
trailing commas allowed, and a key the type does not have, or a null where it allows none, an error.

**Loading** is asynchronous (`Assets.LoadAsync`): the file is read on a worker, once however many ask, and every caller
gets the same object; what it names by handle is loaded by whoever needs it, and a failure is an error the caller decides
what to do with. A loader or post-processor that throws fails that file. One asking while an asset is made again, after
a change, waits for what it is made into. A caller's cancellation token only stops its own wait.

**Post-processing.** Once its loader has made it, a gem's `ILoadPostProcessor<T>` for the type, if the project lists
one, works on the asset before anyone gets it, on every load and reload: a model gets its levels of detail, a pipeline
its passes. What is slow to make that way is kept in [the cache](../../Core/README.md#the-cache), checked against the
asset's version, a hash of its file and sidecar, so it is made again only when either changes. What a post-processor
loads as it works is what the asset is made from (`Assets.Dependencies`), and an asset made from itself, a pipeline
that brings itself in, directly or through others, fails that load rather than waiting for itself.

## Lifetime

- **Kept while in use.** A loaded asset is kept for as long as anything that uses it holds it, then let go, and loaded
  again when it is next asked for, so what is not in use is not in memory. Nothing counts references and nothing
  sets a budget: whoever uses an asset holds it, and lets it go when it is done.
- **Gone files.** An asset whose file is gone is let go.
- **Made assets.** An asset a gem made rather than read from a file, such as a card's atlas, is handed out under a
  handle the gem derives (`Assets.Add`), and found by it for as long as anything holds it.
- **Failures are remembered** until the file or its sidecar changes, so a broken file fails once, not every frame.
- **The version** of an asset (`Assets.Version`) is an XxHash3 of its file and its sidecar's settings, read the first
  time it is asked for, and again once the file changes.

## Hot reload

Core watches the roots, and takes changes in once the files have been left alone for 300 ms (an editor saving a file,
or a build copying a folder, writes several times in a row): it indexes them again and compares the new index with the
old. A change to a `.meta` counts as a change to its asset. What it tells, on `Events`:

| Change | Message |
| --- | --- |
| A loaded asset's file or sidecar was written | `AssetReloaded<T>`, with the asset as it is now, even when nothing holds it any more, so whoever kept only what it made from it hears of it. One that no longer loads is an error, and what loaded before stays. |
| An asset something was made from was made again, or is gone | That is made again after it, then what was made from that, and so on out, each an `AssetReloaded<T>`: editing a pass makes again every pipeline it is in. |
| Files came, went, moved or were written | `AssetsChanged`, once what changed has loaded again. A moved file keeps its id. |

Every sidecar is read again each time, so a broken one, or a duplicate id, is logged again after each change until it
is fixed.

Whatever holds an asset watches these and takes the new one in; what a gem does on a change is in its README.

Every root is watched where the engine reads it: a project's `Assets/`, and the engine's and the gems' folders, where
they are in any build but a release, so an edit here reaches a running engine at once; in a release, their copies
beside Magic.

## Adding an asset

**To the engine** (something every project should have, or a bundled gem needs): put the file here, in the folder
named for its type (`Shaders/`, `Materials/`, …), write its `.meta` with an id of its own, and read that folder's
README for what it must agree with. This folder holds Core's assets and every bundled gem's, so a gem that ships with
Magic has no `Resources/` of its own. Any other gem keeps its files in its own `Resources/`, a root of its own, and a
path two roots have is the first's, so a gem's file may not share a path with the engine's or another gem's.

**To a project**: put the file anywhere under the project's `Assets/`, with its `.meta` beside it. The `.magic` file
names the domains the engine starts from, `entryPoint` and optionally `loading`, and optionally the `player` prefab
put at a domain's spawn:

```json
{
    "name": "Samples",
    "entryPoint": { "id": 3301 },
    "loading": { "id": 700 },
    "player": { "id": 3316 },
    "gems": [ "ArchEcs", "PrefabLoader" ]
}
```

A game's code is its scripts, the `.cs` assets under `Assets/`, each a class named like its file, and the components
and plain types they use beside them: the csproj beside the `.magic` file compiles them, and Core loads what it built
with the project ([Core's README](../../Core/README.md#the-project-file)). It is no gem, so it provides nothing; what a
game needs from a gem, such as a file format, is a gem the project lists.

**From a gem**: a gem adds asset *types*, not files. It can declare an asset type, which needs a default like every
type: an `IJsonAsset<T>` or `ITextAsset<T>` needs nothing more, and any other `IAsset<T>` a gem that provides the
`IAssetLoader<T>` for its format by implementing it. The files it needs itself, such as a renderer's shaders or its
type's default, are in this folder for a gem that ships with Magic, and in the gem's own `Resources/` for any other; a
project's live in its `Assets/`.
