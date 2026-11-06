using System.Collections.Immutable;
using Magic.Utils;

namespace Magic.Files.Pipeline;

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

    public required AssetImportRun Import { get; init; }

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
    /// What a caller gets once it has loaded.
    /// </summary>
    public Made Outcome => Current ?? new Made(Result.Failure(Error), 0);
}

/// <summary>
/// What has been asked for and made, and what makes it: which assets there are is <see cref="AssetDatabase"/>'s. A value,
/// replaced whole on the main thread, and read by any thread as it is.
/// </summary>
internal sealed record LoadedAssets
{
    public static LoadedAssets Empty { get; } = new();

    public ImmutableDictionary<ulong, AssetEntry> Entries { get; init; } = [];

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
    public ImmutableDictionary<string, Type> Types { get; init; } = [];

    /// <summary>
    /// The gem that reads each asset type.
    /// </summary>
    public ImmutableDictionary<Type, object> Importers { get; init; } = [];

    /// <summary>
    /// The gem that writes each asset type.
    /// </summary>
    public ImmutableDictionary<Type, object> Exporters { get; init; } = [];

    /// <summary>
    /// The defaults and failures found missing, each said once.
    /// </summary>
    public ImmutableHashSet<string> ReportedMissing { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether asset <paramref name="id"/> was made from <paramref name="other"/>, however far down.
    /// </summary>
    public bool IsMadeFrom(ulong id, ulong other) =>
        Walk([id], one => Entries.TryGetValue(one, out AssetEntry? entry) ? entry.MadeFrom : []).Contains(other);

    /// <summary>
    /// What was made from any of <paramref name="changed"/>, however far up.
    /// </summary>
    public HashSet<ulong> GetMadeFromThese(IEnumerable<ulong> changed)
    {
        Dictionary<ulong, List<ulong>> madeInto = [];
        foreach ((ulong id, AssetEntry entry) in Entries)
        {
            foreach (ulong source in entry.MadeFrom)
            {
                if (!madeInto.TryGetValue(source, out List<ulong>? of))
                {
                    of = [];
                    madeInto.Add(source, of);
                }

                of.Add(id);
            }
        }

        return Walk(changed, id => madeInto.TryGetValue(id, out List<ulong>? of) ? of : []);
    }

    /// <summary>
    /// The assets whose importers listed a folder one of <paramref name="touched"/> is in.
    /// </summary>
    public IEnumerable<ulong> GetListersOf(IReadOnlySet<string> touched)
    {
        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in touched)
        {
            int slash = path.LastIndexOf('/');
            folders.Add(slash < 0 ? string.Empty : path[..slash]);
        }

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

    /// <summary>
    /// These assets with asset <paramref name="id"/> to be read.
    /// </summary>
    public LoadedAssets Want(ulong id) =>
        this with
        {
            Entries = Entries.SetItem(id, Entries[id] with { Stage = AssetStage.Wanted, Landing = null }),
            Wanted = Wanted.Add(id),
        };

    /// <summary>
    /// These assets with what a load of asset <paramref name="id"/> made about to land, in place of what was about to.
    /// </summary>
    public LoadedAssets Land(ulong id, AssetEntry entry, Made made) =>
        this with
        {
            Entries = Entries.SetItem(id, entry with { Stage = AssetStage.Landed, Landing = made }),
            Landed = Landed.Contains(id) ? Landed : Landed.Add(id),
        };

    /// <summary>
    /// What is reached from <paramref name="start"/>, however far down <paramref name="neighbours"/>, each once.
    /// </summary>
    private static HashSet<ulong> Walk(IEnumerable<ulong> start, Func<ulong, IEnumerable<ulong>> neighbours)
    {
        HashSet<ulong> found = [];
        List<ulong> level = [.. start];
        while (level.Count > 0)
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
}
