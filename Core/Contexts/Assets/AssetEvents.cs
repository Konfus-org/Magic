namespace Magic.Contexts.Assets;

// Published by AssetManager.ProcessChanges on the main thread, once per settled file change. Paths are
// relative to the folder the asset lives under, with forward slashes. Ids are what a Handle<T> carries.

/// <summary>A file appeared under a watched folder (or a folder was added) and has an id now.</summary>
public readonly record struct AssetAdded(ulong Id, string Path);

/// <summary>The file or its sidecar was written; anyone holding the asset should load it again.</summary>
public readonly record struct AssetModified(ulong Id, string Path);

/// <summary>The file is gone; its id no longer resolves.</summary>
public readonly record struct AssetRemoved(ulong Id, string Path);

/// <summary>The file and its sidecar moved or were renamed; the id is the same.</summary>
public readonly record struct AssetMoved(ulong Id, string OldPath, string Path);
