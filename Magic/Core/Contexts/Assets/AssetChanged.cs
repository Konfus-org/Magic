namespace Magic.Contexts.Assets;

/// <summary>
/// What happened to an asset's file, in an <see cref="AssetChanged"/>.
/// </summary>
public enum AssetChange : byte
{
    /// <summary>
    /// A file appeared under a watched asset folder and has an id now.
    /// </summary>
    Added,

    /// <summary>
    /// The file or its sidecar was written; whoever holds the asset loads it again.
    /// </summary>
    Modified,

    /// <summary>
    /// The file moved or was renamed; the id is the same, <see cref="AssetChanged.OldPath"/> is where it was.
    /// </summary>
    Moved,

    /// <summary>
    /// The file is gone; its id no longer resolves.
    /// </summary>
    Removed,
}

/// <summary>
/// An asset's file changed on disk: <see cref="Path"/> is relative to its root, forward slashes.
/// </summary>
public readonly record struct AssetChanged(AssetChange Change, ulong Id, string Path, string? OldPath = null);
