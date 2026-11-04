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
    /// To be read, once every gem is made.
    /// </summary>
    Wanted,

    /// <summary>
    /// Being read on a worker.
    /// </summary>
    Loading,

    /// <summary>
    /// Read, and about to become what callers get.
    /// </summary>
    Landed,

    /// <summary>
    /// Made, or failed, and kept.
    /// </summary>
    Loaded,
}

/// <summary>
/// Runs the pipeline that loads asset <paramref name="id"/> as its type, by <paramref name="loader"/>, the gem that reads
/// the type, or by Core itself when there is none: what a load of the type runs on a worker.
/// </summary>
internal delegate Task<Made> AssetReader(
    ulong id,
    IndexedFile file,
    object? loader,
    IProgress<LoadProgress>? progress,
    CancellationToken cancel
);

/// <summary>
/// Reads an asset's file as its type, by Core itself, loading nothing: for finding what it holds.
/// </summary>
internal delegate Result<object> AssetBuiltInReader(ReadOnlyMemory<byte> bytes, JsonElement settings);

/// <summary>
/// An asset type, as a <c>.meta</c>'s <c>"type"</c> names it: the type, and how its files are read.
/// </summary>
internal sealed record AssetType(Type Type, AssetReader Read, AssetBuiltInReader ReadBuiltIn);

/// <summary>
/// What a load of an asset gave: the asset, of its type, or why there is none; its version, a hash of its file and
/// <c>.meta</c>; and the ids of the handles it holds, when they were looked for.
/// </summary>
internal readonly record struct Made(Result<object> Value, ulong Version, ImmutableArray<ulong> Handles = default);

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
    /// What a load just made, while it is <see cref="AssetStage.Landed"/>.
    /// </summary>
    public Made? Landing { get; init; }

    /// <summary>
    /// Why it failed, when it has never loaded; kept until its file changes.
    /// </summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// The ids of what it loaded as it was made last, or has loaded so far as it is made now.
    /// </summary>
    public ImmutableArray<ulong> Dependencies { get; init; } = [];

    /// <summary>
    /// The folders its loader listed as it was made last, or has so far as it is made now.
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
    /// What a caller gets once it has loaded.
    /// </summary>
    public Made Outcome => Current ?? new Made(Result.Failure(Error), 0);
}

/// <summary>
/// Everything known about the assets at one moment: a value, replaced whole on the main thread by the pipeline over an
/// <see cref="AssetsChangeList"/>, and read by any thread as it is.
/// </summary>
internal sealed record AssetSnapshot
{
    public static AssetSnapshot Empty { get; } = new();

    public AssetIndex Index { get; init; } = AssetIndex.Empty;

    /// <summary>
    /// The folders assets are found under, in order, the first winning a path two of them have.
    /// </summary>
    public ImmutableArray<string> Directories { get; init; } = [];

    public ImmutableDictionary<ulong, AssetEntry> Assets { get; init; } = [];

    /// <summary>
    /// The assets to start reading, so a run with none costs nothing. One that has moved on since is passed over.
    /// </summary>
    public ImmutableHashSet<ulong> Wanted { get; init; } = [];

    /// <summary>
    /// The assets whose reads have finished, in the order they did, so what depends on one lands after it.
    /// </summary>
    public ImmutableArray<ulong> Landed { get; init; } = [];

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
    /// The gem that writes each asset type.
    /// </summary>
    public ImmutableDictionary<Type, object> Writers { get; init; } = [];

    /// <summary>
    /// The defaults and failures found missing, each said once.
    /// </summary>
    public ImmutableHashSet<string> ReportedMissing { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether every gem is made, so each type's loader is there and asset loads run.
    /// </summary>
    public bool IsLoadingEnabled { get; init; }

    /// <summary>
    /// The paths the watches saw change, from their asset directories, not yet settled.
    /// </summary>
    public ImmutableHashSet<string> Touched { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the watches last saw a change (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).
    /// </summary>
    public long TouchedTimestamp { get; init; }

    /// <summary>
    /// Whether a worker is reading the asset directories again, so another read waits for it.
    /// </summary>
    public bool IsIndexing { get; init; }

    /// <summary>
    /// What is reached from <paramref name="start"/>, down to <paramref name="levels"/> levels of
    /// <paramref name="neighbours"/>, each once.
    /// </summary>
    public static HashSet<ulong> Walk(IEnumerable<ulong> start, int levels, Func<ulong, IEnumerable<ulong>> neighbours)
    {
        HashSet<ulong> found = [];
        List<ulong> level = [.. start];
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong id in level)
            {
                foreach (ulong neighbour in neighbours(id))
                {
                    if (found.Add(neighbour))
                        next.Add(neighbour);
                }
            }

            level = next;
        }

        return found;
    }

    /// <summary>
    /// What messages call asset <paramref name="id"/>: its path, or its id when no file has it.
    /// </summary>
    public string GetName(ulong id) =>
        Index.ById.TryGetValue(id, out IndexedFile? file) ? file.Path : $"The asset {id}";

    /// <summary>
    /// What <paramref name="asset"/> depends on, down to <paramref name="levels"/> levels: 1 for what it loads itself,
    /// <see cref="AssetDatabase.AllLevels"/> for everything under it.
    /// </summary>
    public HashSet<ulong> GetDependencies(ulong asset, int levels) =>
        Walk([asset], levels, id => Assets.TryGetValue(id, out AssetEntry? entry) ? entry.Dependencies : []);

    /// <summary>
    /// What depends on any of <paramref name="changed"/>, up to <paramref name="levels"/> levels: 1 for what loads one of
    /// them itself, <see cref="AssetDatabase.AllLevels"/> for everything above them.
    /// </summary>
    public HashSet<ulong> GetDependents(IEnumerable<ulong> changed, int levels)
    {
        Dictionary<ulong, List<ulong>> dependents = [];
        foreach ((ulong id, AssetEntry entry) in Assets)
        {
            foreach (ulong dependency in entry.Dependencies)
            {
                if (!dependents.TryGetValue(dependency, out List<ulong>? of))
                {
                    of = [];
                    dependents.Add(dependency, of);
                }

                of.Add(id);
            }
        }

        return Walk(changed, levels, id => dependents.TryGetValue(id, out List<ulong>? of) ? of : []);
    }

    /// <summary>
    /// The assets whose loaders listed a folder one of <paramref name="touched"/> is in.
    /// </summary>
    public IEnumerable<ulong> GetListersOf(IReadOnlySet<string> touched)
    {
        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in touched)
        {
            int slash = path.LastIndexOf('/');
            folders.Add(slash < 0 ? string.Empty : path[..slash]);
        }

        foreach ((ulong id, AssetEntry entry) in Assets)
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

    /// <summary>
    /// This snapshot with asset <paramref name="id"/> to be read.
    /// </summary>
    public AssetSnapshot Want(ulong id) =>
        this with
        {
            Assets = Assets.SetItem(id, Assets[id] with { Stage = AssetStage.Wanted, Landing = null }),
            Wanted = Wanted.Add(id),
        };

    /// <summary>
    /// This snapshot with what a load of asset <paramref name="id"/> made about to land, in place of what was about to.
    /// </summary>
    public AssetSnapshot Land(ulong id, AssetEntry entry, Made made) =>
        this with
        {
            Assets = Assets.SetItem(id, entry with { Stage = AssetStage.Landed, Landing = made }),
            Landed = Landed.Contains(id) ? Landed : Landed.Add(id),
        };
}
