using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Where an asset is in being loaded.
/// </summary>
internal enum AssetStage : byte
{
    /// <summary>
    /// To be read once the gem expected to read its type registers, or is not made.
    /// </summary>
    Wanted,

    /// <summary>
    /// Being read on a worker.
    /// </summary>
    Loading,

    /// <summary>
    /// Made, or failed, and kept.
    /// </summary>
    Loaded,
}

/// <summary>
/// Imports asset <paramref name="id"/> as its type, by <paramref name="importer"/>, the gem that
/// reads the type, or by Core itself when there is none: what a load of the type runs on a worker.
/// </summary>
internal delegate Task<Made> AssetImportRun(
    ulong id,
    IndexedFile file,
    object? importer,
    IProgress<LoadProgress>? progress,
    CancellationToken cancel
);

/// <summary>
/// Keeps what one preprocessor makes of asset <paramref name="id"/>, made from <paramref name="version"/> of its file, in
/// the cache, unless it is there already. On a worker.
/// </summary>
internal delegate Task AssetPreprocessRun(ulong id, ulong version, object asset, CancellationToken cancel);

/// <summary>
/// Reads an asset's file as its type, by Core itself, loading nothing: for finding what it holds.
/// </summary>
internal delegate Result<object> AssetBuiltInReader(ReadOnlyMemory<byte> bytes, JsonElement settings);

/// <summary>
/// An asset type, as a <c>.meta</c>'s <c>"type"</c> names it: the type, and how its files are read.
/// </summary>
internal sealed record AssetType(Type Type, AssetImportRun Import, AssetBuiltInReader ReadBuiltIn);

/// <summary>
/// What a load of an asset gave: the asset, of its type, or why there is none; its version, a hash of its file and
/// <c>.meta</c>; and the ids of the handles it holds, when they were looked for.
/// </summary>
internal readonly record struct Made(Result<object> Value, ulong Version, ImmutableArray<ulong> Handles = default);

/// <summary>
/// What a load of an asset made, held only as long as something else holds it, so the GC frees it once nothing does; and,
/// as <see cref="Made"/> has them, its version and the ids of the handles it holds.
/// </summary>
internal sealed record KeptAsset(WeakReference<object> Value, ulong Version, ImmutableArray<ulong> Handles);

/// <summary>
/// One asset asked for, and where it is in being loaded.
/// </summary>
internal sealed record AssetEntry
{
    /// <summary>
    /// The type it was first asked for as, the one it loads as.
    /// </summary>
    public required Type Type { get; init; }

    public required AssetImportRun Import { get; init; }

    public required AssetStage Stage { get; init; }

    /// <summary>
    /// What it made last, of those that worked: what every caller gets, a failed reload included, while something holds
    /// it.
    /// </summary>
    public KeptAsset? Current { get; init; }

    /// <summary>
    /// Why it failed, when nothing it made before is held; kept until its file changes.
    /// </summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// The ids of what its importer loaded as it was made last, or has so far as it is made now: when one of them is made
    /// again, so is it.
    /// </summary>
    public ImmutableArray<ulong> MadeFrom { get; init; } = [];

    /// <summary>
    /// The folders its importer listed as it was made last, or has so far as it is made now: when one of them changes, it
    /// is made again.
    /// </summary>
    public ImmutableArray<string> Folders { get; init; } = [];

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
    /// What it made last, while something holds it; null when it failed, or what it made was let go.
    /// </summary>
    public object? GetValue() => Current is { } kept && kept.Value.TryGetTarget(out object? value) ? value : null;

    /// <summary>
    /// What a caller gets once it has loaded: what it made, or why it failed; null when what it made was let go, so it is
    /// read again.
    /// </summary>
    public Made? GetOutcome()
    {
        if (Current is null)
            return new Made(Result.Failure(Error), 0);

        return GetValue() is { } value
            ? new Made(Result.Success<object>(value), Current.Version, Current.Handles)
            : null;
    }
}

/// <summary>
/// What has been asked for and made, and what makes it: which assets there are is <see cref="AssetDatabase"/>'s. A value,
/// replaced whole under the assets' lock, and read by any thread as it is.
/// </summary>
internal sealed record LoadedAssets
{
    public static LoadedAssets Empty { get; } = new();

    public ImmutableDictionary<ulong, AssetEntry> Entries { get; init; } = [];

    /// <summary>
    /// Every asset type Magic and the gems hold, by the name a <c>.meta</c> gives it.
    /// </summary>
    public ImmutableDictionary<string, AssetType> Types { get; init; } = [];

    /// <summary>
    /// The gem that reads each asset type.
    /// </summary>
    public ImmutableDictionary<Type, object> Importers { get; init; } = [];

    /// <summary>
    /// The gem that writes each asset type.
    /// </summary>
    public ImmutableDictionary<Type, object> Exporters { get; init; } = [];

    /// <summary>
    /// What each asset type's load runs after the import, a run for each preprocessor registered for it.
    /// </summary>
    public ImmutableDictionary<Type, ImmutableArray<AssetPreprocessRun>> Preprocessors { get; init; } = [];

    /// <summary>
    /// The asset types each gem not made yet will read, by the gem's type: a load of one waits for it.
    /// </summary>
    public ImmutableDictionary<Type, ImmutableArray<Type>> Expected { get; init; } = [];

    /// <summary>
    /// The folders of the defaults found missing, each said once.
    /// </summary>
    public ImmutableHashSet<string> ReportedMissing { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether asset <paramref name="id"/> was made from <paramref name="other"/>, however far down.
    /// </summary>
    public bool IsMadeFrom(ulong id, ulong other) =>
        AssetDatabase
            .Walk(
                [id],
                AssetManager.AllLevels,
                one => Entries.TryGetValue(one, out AssetEntry? entry) ? entry.MadeFrom : []
            )
            .Contains(other);

    /// <summary>
    /// What was made from any of <paramref name="changed"/>, however far up.
    /// </summary>
    public HashSet<ulong> GetMadeFromThese(IEnumerable<ulong> changed)
    {
        ILookup<ulong, ulong> madeInto = Entries
            .SelectMany(pair => pair.Value.MadeFrom, (pair, source) => (Source: source, Id: pair.Key))
            .ToLookup(made => made.Source, made => made.Id);

        return AssetDatabase.Walk(changed, AssetManager.AllLevels, id => madeInto[id]);
    }

    /// <summary>
    /// The assets whose importers listed a folder one of <paramref name="touched"/> is in.
    /// </summary>
    public IEnumerable<ulong> GetListersOf(IReadOnlySet<string> touched)
    {
        HashSet<string> folders = new(touched.Select(AssetIndex.GetFolder), StringComparer.OrdinalIgnoreCase);

        foreach ((ulong id, AssetEntry entry) in Entries)
        {
            foreach (string folder in entry.Folders)
            {
                if (folders.Contains(folder))
                {
                    yield return id;
                    break;
                }
            }
        }
    }
}
