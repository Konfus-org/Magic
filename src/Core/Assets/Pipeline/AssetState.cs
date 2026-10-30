using System.Collections.Immutable;
using System.Text.Json;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Where an asset is in being loaded.
/// </summary>
internal enum AssetStage : byte
{
    /// <summary>
    /// Asked for before every gem was made; loaded once they are.
    /// </summary>
    Queued,

    Loading,

    /// <summary>
    /// Made, or failed, and kept.
    /// </summary>
    Loaded,
}

/// <summary>
/// An asset's file as read from disk, before its <c>.meta</c>'s settings are read as its type's.
/// </summary>
/// <param name="Id">The asset's id.</param>
/// <param name="Path">Where it is from its asset directory, with <c>/</c>.</param>
/// <param name="Bytes">What the file holds.</param>
/// <param name="Settings">Its <c>.meta</c> without the id and the type.</param>
/// <param name="Version">A hash of the file and its <c>.meta</c>.</param>
internal readonly record struct AssetBytes(
    ulong Id,
    string Path,
    ReadOnlyMemory<byte> Bytes,
    JsonElement Settings,
    ulong Version
);

/// <summary>
/// Reads an asset's file as its type, by <paramref name="loader"/>, the gem that reads the type, or by Core itself when
/// there is none: what a load of the type runs on a worker.
/// </summary>
internal delegate Task<Result<object>> AssetReader(
    AssetBytes file,
    object? loader,
    IProgress<LoadProgress>? progress,
    CancellationToken cancel
);

/// <summary>
/// An asset type, as a <c>.meta</c>'s <c>"type"</c> names it: the type, and how its files are read.
/// </summary>
internal sealed record AssetType(Type Type, AssetReader Read);

/// <summary>
/// What a load of an asset gave: the asset, of its type, or why there is none; and its version, a hash of its file and
/// <c>.meta</c>.
/// </summary>
internal readonly record struct Made(Result<object> Value, ulong Version);

/// <summary>
/// One asset asked for or added, and where it is in being loaded.
/// </summary>
internal sealed record AssetEntry
{
    /// <summary>
    /// The type it was first asked for as, the one it loads as.
    /// </summary>
    public required Type Type { get; init; }

    public required AssetReader Read { get; init; }

    public required AssetStage Stage { get; init; }

    /// <summary>
    /// What it made last, of those that worked: what every caller gets, a failed reload included.
    /// </summary>
    public Made? Current { get; init; }

    /// <summary>
    /// Why it failed, when it has never loaded; kept until its file changes.
    /// </summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// The ids of what it loaded as it was made last, or has loaded so far as it is made now.
    /// </summary>
    public ImmutableArray<ulong> Dependencies { get; init; } = [];

    /// <summary>
    /// Whether its file changed while it loaded, so it loads again once this load lands.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>
    /// What the first caller watches its first load with.
    /// </summary>
    public IProgress<LoadProgress>? Progress { get; init; }

    /// <summary>
    /// The callers waiting for its load to land.
    /// </summary>
    public ImmutableArray<TaskCompletionSource<Made>> Waiting { get; init; } = [];

    /// <summary>
    /// What a caller gets once it has loaded.
    /// </summary>
    public Made Outcome => Current ?? new Made(Result.Failure(Error), 0);
}

/// <summary>
/// Everything the asset manager knows at one moment: a value, replaced whole by each <see cref="AssetPipeline.Pump"/>,
/// and read by any thread as it is.
/// </summary>
internal sealed record AssetState
{
    public static AssetState Empty { get; } = new();

    public AssetIndex Index { get; init; } = AssetIndex.Empty;

    /// <summary>
    /// The folders assets are found under, in order, the first winning a path two of them have.
    /// </summary>
    public ImmutableArray<string> Directories { get; init; } = [];

    public ImmutableDictionary<ulong, AssetEntry> Assets { get; init; } = [];

    /// <summary>
    /// Every asset type Magic and the gems hold, by the name a <c>.meta</c> gives it.
    /// </summary>
    public ImmutableDictionary<string, AssetType> Types { get; init; } = [];

    /// <summary>
    /// What each asset scanned so far depends on, found without loading anything: kept until its file changes.
    /// </summary>
    public ImmutableDictionary<ulong, ImmutableArray<ulong>> Scanned { get; init; } = [];

    /// <summary>
    /// The gem that reads each asset type.
    /// </summary>
    public ImmutableDictionary<Type, object> Loaders { get; init; } = [];

    /// <summary>
    /// The defaults and failures found missing, each said once.
    /// </summary>
    public ImmutableHashSet<string> ReportedMissing { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether every gem is made, so each type's loader is there and asset loads run.
    /// </summary>
    public bool IsLoadingEnabled { get; init; }
}
