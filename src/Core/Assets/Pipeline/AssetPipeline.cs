using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// What happened that the asset manager takes in: handed to <see cref="AssetPipeline.Pump"/> one at a time.
/// </summary>
internal abstract record AssetInput;

/// <summary>
/// A caller asked for asset <paramref name="Id"/> as <paramref name="Type"/>, to be told on
/// <paramref name="Waiter"/>; a loader did, as what asset <paramref name="Dependent"/> depends on, when it is not 0.
/// </summary>
internal sealed record AssetRequested(
    ulong Id,
    Type Type,
    AssetReader Read,
    IProgress<LoadProgress>? Progress,
    TaskCompletionSource<Made> Waiter,
    ulong Dependent
) : AssetInput;

/// <summary>
/// A worker finished reading asset <paramref name="Id"/>'s file, which made <paramref name="Made"/>.
/// </summary>
internal sealed record AssetFileRead(ulong Id, Made Made) : AssetInput;

/// <summary>
/// The asset directories were read again, as <paramref name="Index"/>, after the watches saw
/// <paramref name="Touched"/>, paths from their asset directories.
/// </summary>
internal sealed record AssetDirectoriesReindexed(AssetIndex Index, ImmutableHashSet<string> Touched) : AssetInput;

internal sealed record AssetLoaderAdded(Type Type, object Loader) : AssetInput;

internal sealed record AssetLoaderRemoved(Type Type, object Loader) : AssetInput;

/// <summary>
/// A gem made <paramref name="Asset"/> and hands it out as asset <paramref name="Id"/>.
/// </summary>
internal sealed record AssetAdded(ulong Id, Type Type, AssetReader Read, object Asset) : AssetInput;

/// <summary>
/// A built-in default or failure, <paramref name="Key"/>, was asked for and is missing, as <paramref name="Message"/>
/// says.
/// </summary>
internal sealed record BuiltInAssetMissing(string Key, string Message) : AssetInput;

/// <summary>
/// The first pump came, once every gem is made, so each type's loader is there, and asset loads may run.
/// </summary>
internal sealed record AssetLoadingEnabled : AssetInput;

/// <summary>
/// A scan of asset <paramref name="Id"/>'s file found what it depends on, without loading them.
/// </summary>
internal sealed record AssetDependenciesScanned(ulong Id, ImmutableArray<ulong> Dependencies) : AssetInput;

/// <summary>
/// What the asset manager's shell does for a <see cref="AssetPipeline.Pump"/>.
/// </summary>
internal abstract record AssetAction;

/// <summary>
/// Read <paramref name="File"/> on a worker as asset <paramref name="Id"/>, a <paramref name="Type"/>, and hand back
/// what it made as <see cref="AssetFileRead"/>.
/// </summary>
internal sealed record ReadAssetFile(
    ulong Id,
    Type Type,
    IndexedFile File,
    AssetReader Read,
    object? Loader,
    IProgress<LoadProgress>? Progress
) : AssetAction;

/// <summary>
/// Give <paramref name="Waiter"/>, a caller waiting for an asset, what its load made.
/// </summary>
internal sealed record CompleteAssetLoad(TaskCompletionSource<Made> Waiter, Made Made) : AssetAction;

/// <summary>
/// How the asset manager's state changes: a pure function from the state and one input to the next state and what the
/// shell does for it, as a fold over everything the manager takes in. It only logs.
/// </summary>
internal static class AssetPipeline
{
    /// <summary>
    /// The state after <paramref name="input"/>, and what to do for it.
    /// </summary>
    public static (AssetState State, ImmutableArray<AssetAction> Actions) Pump(AssetState state, AssetInput input)
    {
        ImmutableArray<AssetAction>.Builder actions = ImmutableArray.CreateBuilder<AssetAction>();
        AssetState next = input switch
        {
            AssetRequested requested => Request(state, requested, actions),
            AssetFileRead read => TakeRead(state, read, actions),
            AssetDirectoriesReindexed reindexed => Reindex(state, reindexed, actions),
            AssetLoaderAdded added => AddLoader(state, added),
            AssetLoaderRemoved removed => RemoveLoader(state, removed),
            AssetAdded added => Add(state, added, actions),
            BuiltInAssetMissing missing => ReportMissing(state, missing),
            AssetLoadingEnabled => EnableLoading(state, actions),
            AssetDependenciesScanned scanned => state with
            {
                Scanned = state.Scanned.SetItem(scanned.Id, scanned.Dependencies),
            },
            _ => throw new ArgumentException(
                $"{input.GetType().Name} is no input the asset manager takes.",
                nameof(input)
            ),
        };

        return (next, actions.ToImmutable());
    }

    /// <summary>
    /// What <paramref name="asset"/> depends on, as <paramref name="assets"/> say, down to <paramref name="levels"/>
    /// levels: 1 for what it loads itself, <see cref="AssetManager.AllLevels"/> for everything under it.
    /// </summary>
    public static HashSet<ulong> GetDependencies(ulong asset, int levels, ImmutableDictionary<ulong, AssetEntry> assets)
    {
        HashSet<ulong> found = [];
        List<ulong> level = [asset];
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong id in level)
            {
                if (!assets.TryGetValue(id, out AssetEntry? entry))
                    continue;

                foreach (ulong dependency in entry.Dependencies)
                {
                    if (found.Add(dependency))
                        next.Add(dependency);
                }
            }

            level = next;
        }

        return found;
    }

    /// <summary>
    /// What depends on any of <paramref name="changed"/>, as <paramref name="assets"/> say, up to
    /// <paramref name="levels"/> levels: 1 for what loads one of them itself, <see cref="AssetManager.AllLevels"/> for
    /// everything above them.
    /// </summary>
    public static HashSet<ulong> GetDependents(
        IEnumerable<ulong> changed,
        int levels,
        ImmutableDictionary<ulong, AssetEntry> assets
    )
    {
        HashSet<ulong> found = [];
        HashSet<ulong> level = [.. changed];
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            HashSet<ulong> next = [];
            foreach ((ulong id, AssetEntry entry) in assets)
            {
                if (found.Contains(id))
                    continue;

                foreach (ulong dependency in entry.Dependencies)
                {
                    if (!level.Contains(dependency))
                        continue;

                    found.Add(id);
                    next.Add(id);
                    break;
                }
            }

            level = next;
        }

        return found;
    }

    /// <summary>
    /// What messages call asset <paramref name="id"/>: its path, or its id when no file has it.
    /// </summary>
    public static string GetName(AssetIndex index, ulong id) =>
        index.ById.TryGetValue(id, out IndexedFile? file) ? file.Path : $"The asset {id}";

    /// <summary>
    /// Answers <paramref name="requested"/>: with what loaded, by waiting for a load on its way, or by starting one;
    /// with a failure when it is another type, or, asked for by a loader, when it depends on what asks for it.
    /// </summary>
    private static AssetState Request(
        AssetState state,
        AssetRequested requested,
        ImmutableArray<AssetAction>.Builder actions
    )
    {
        ulong id = requested.Id;
        ulong dependent = requested.Dependent;
        if (dependent != 0)
        {
            if (id == dependent || GetDependencies(id, AssetManager.AllLevels, state.Assets).Contains(dependent))
            {
                string name = GetName(state.Index, dependent);
                actions.Add(
                    Fail(requested.Waiter, $"{name} depends on itself: something it loads as it is made loads it.")
                );
                return state;
            }

            state = RecordDependency(state, dependent, id);
        }

        if (state.Index.ById.TryGetValue(id, out IndexedFile? file) && file.Type != requested.Type.Name)
        {
            actions.Add(Fail(requested.Waiter, $"{file.Path} is a {file.Type}, so it is not a {requested.Type.Name}."));
            return state;
        }

        if (!state.Assets.TryGetValue(id, out AssetEntry? entry))
        {
            entry = new AssetEntry
            {
                Type = requested.Type,
                Read = requested.Read,
                Stage = AssetStage.Queued,
                Progress = requested.Progress,
                Waiting = [requested.Waiter],
            };
            state = state with { Assets = state.Assets.SetItem(id, entry) };

            return state.IsLoadingEnabled ? StartRead(state, id, actions) : state;
        }

        if (entry.Type != requested.Type)
        {
            string name = GetName(state.Index, id);
            actions.Add(
                Fail(
                    requested.Waiter,
                    $"{name} is loaded as a {entry.Type.Name}, so it is not a {requested.Type.Name}."
                )
            );
            return state;
        }

        if (entry.Stage == AssetStage.Loaded)
        {
            actions.Add(new CompleteAssetLoad(requested.Waiter, entry.Outcome));
            return state;
        }

        return state with
        {
            Assets = state.Assets.SetItem(id, entry with { Waiting = entry.Waiting.Add(requested.Waiter) }),
        };
    }

    /// <summary>
    /// Takes in what a read of an asset's file made, unless the file changed meanwhile, when it is read again.
    /// </summary>
    private static AssetState TakeRead(
        AssetState state,
        AssetFileRead read,
        ImmutableArray<AssetAction>.Builder actions
    )
    {
        // Gone while it was read: nobody is told, as its waiters were when it went.
        if (!state.Assets.TryGetValue(read.Id, out AssetEntry? entry))
            return state;

        return entry.IsStale ? StartRead(state, read.Id, actions) : Finish(state, read.Id, read.Made, actions);
    }

    /// <summary>
    /// Takes in the asset directories read again: each asset loaded whose file changed, is another file now, or
    /// depends on one of those, and so on out, loads again, each after its dependencies, as a load waits for those on
    /// their way; one whose file is gone is let go.
    /// </summary>
    private static AssetState Reindex(
        AssetState state,
        AssetDirectoriesReindexed reindexed,
        ImmutableArray<AssetAction>.Builder actions
    )
    {
        AssetIndex before = state.Index;
        AssetIndex now = reindexed.Index;
        HashSet<ulong> changed = AssetIndex.Diff(before, now, reindexed.Touched);
        HashSet<ulong> reload = [.. changed, .. GetDependents(changed, AssetManager.AllLevels, state.Assets)];
        state = state with { Index = now, Scanned = state.Scanned.RemoveRange(changed) };
        int reloading = 0;
        foreach (ulong id in reload)
        {
            if (!state.Assets.TryGetValue(id, out AssetEntry? entry))
                continue;

            if (!now.ById.ContainsKey(id))
            {
                // One a gem added has no file, and stays.
                if (!before.ById.TryGetValue(id, out IndexedFile? gone))
                    continue;

                foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
                {
                    actions.Add(Fail(waiter, $"{gone.Path} is gone."));
                }

                state = state with { Assets = state.Assets.Remove(id) };
                continue;
            }

            if (entry.Stage == AssetStage.Queued)
                continue;

            reloading++;
            state =
                entry.Stage == AssetStage.Loading
                    ? state with
                    {
                        Assets = state.Assets.SetItem(id, entry with { IsStale = true }),
                    }
                    : StartRead(state, id, actions);
        }

        if (reloading > 0)
            Log.Info($"Reloading {reloading} assets.");

        return state;
    }

    /// <summary>
    /// Has <paramref name="added"/>'s loader read its type; a type another reads already stays that one's, and is an
    /// error.
    /// </summary>
    private static AssetState AddLoader(AssetState state, AssetLoaderAdded added)
    {
        if (state.Loaders.TryGetValue(added.Type, out object? reads))
        {
            Log.Error(
                $"{added.Loader.GetType().Name} reads {added.Type.Name}s, which {reads.GetType().Name} reads already, "
                    + "so it does not."
            );
            return state;
        }

        return state with
        {
            Loaders = state.Loaders.Add(added.Type, added.Loader),
        };
    }

    private static AssetState RemoveLoader(AssetState state, AssetLoaderRemoved removed) =>
        state.Loaders.TryGetValue(removed.Type, out object? reads) && ReferenceEquals(reads, removed.Loader)
            ? state with
            {
                Loaders = state.Loaders.Remove(removed.Type),
            }
            : state;

    /// <summary>
    /// Hands out an asset a gem made, replacing any it added under the id before, a version later; refused when a file
    /// has the id.
    /// </summary>
    private static AssetState Add(AssetState state, AssetAdded added, ImmutableArray<AssetAction>.Builder actions)
    {
        if (state.Index.ById.TryGetValue(added.Id, out IndexedFile? file))
        {
            Log.Error(
                $"A {added.Type.Name} a gem made has the id {added.Id}, which {file.Path} has, so it is not added."
            );
            return state;
        }

        state.Assets.TryGetValue(added.Id, out AssetEntry? before);
        ulong version = (before?.Current?.Version ?? 0) + 1;
        AssetEntry entry = new()
        {
            Type = added.Type,
            Read = added.Read,
            Stage = AssetStage.Loading,
            Waiting = before?.Waiting ?? [],
        };
        state = state with { Assets = state.Assets.SetItem(added.Id, entry) };

        return Finish(state, added.Id, new Made(Result.Success(added.Asset), version), actions);
    }

    /// <summary>
    /// Says a missing default or failure is missing, the first time it is asked for.
    /// </summary>
    private static AssetState ReportMissing(AssetState state, BuiltInAssetMissing missing)
    {
        if (state.ReportedMissing.Contains(missing.Key))
            return state;

        Log.Error(missing.Message);

        return state with
        {
            ReportedMissing = state.ReportedMissing.Add(missing.Key),
        };
    }

    /// <summary>
    /// Lets asset loads run, starting those asked for before every gem was made.
    /// </summary>
    private static AssetState EnableLoading(AssetState state, ImmutableArray<AssetAction>.Builder actions)
    {
        state = state with { IsLoadingEnabled = true };
        foreach ((ulong id, AssetEntry entry) in state.Assets)
        {
            if (entry.Stage == AssetStage.Queued)
                state = StartRead(state, id, actions);
        }

        return state;
    }

    /// <summary>
    /// Starts reading asset <paramref name="id"/>'s file, forgetting what it depended on, as the read records its own;
    /// it fails at once when no file has its id.
    /// </summary>
    private static AssetState StartRead(AssetState state, ulong id, ImmutableArray<AssetAction>.Builder actions)
    {
        AssetEntry entry = state.Assets[id];
        if (!state.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Finish(state, id, new Made(Result.Failure($"No asset has the id {id}."), 0), actions);

        actions.Add(
            new ReadAssetFile(
                id,
                entry.Type,
                file,
                entry.Read,
                state.Loaders.GetValueOrDefault(entry.Type),
                entry.Progress
            )
        );

        return state with
        {
            Assets = state.Assets.SetItem(
                id,
                entry with
                {
                    Stage = AssetStage.Loading,
                    IsStale = false,
                    Dependencies = [],
                    Progress = null,
                }
            ),
        };
    }

    /// <summary>
    /// Takes in what a load of asset <paramref name="id"/> made and tells its waiters: one that failed while what it
    /// made before is kept leaves that in use, and says so.
    /// </summary>
    private static AssetState Finish(AssetState state, ulong id, Made made, ImmutableArray<AssetAction>.Builder actions)
    {
        AssetEntry entry = state.Assets[id];
        AssetEntry done;
        if (made.Value.Ok)
        {
            done = entry with { Current = made, Error = string.Empty };
        }
        else if (entry.Current is not null)
        {
            Log.Error($"{made.Value.Error} What loaded before stays in use.");
            done = entry;
        }
        else
        {
            done = entry with { Error = made.Value.Error };
        }

        done = done with { Stage = AssetStage.Loaded, Waiting = [] };
        foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
        {
            actions.Add(new CompleteAssetLoad(waiter, done.Outcome));
        }

        return state with
        {
            Assets = state.Assets.SetItem(id, done),
        };
    }

    /// <summary>
    /// <paramref name="state"/> with <paramref name="dependency"/> among what asset <paramref name="dependent"/>
    /// depends on.
    /// </summary>
    private static AssetState RecordDependency(AssetState state, ulong dependent, ulong dependency)
    {
        if (!state.Assets.TryGetValue(dependent, out AssetEntry? entry) || entry.Dependencies.Contains(dependency))
            return state;

        return state with
        {
            Assets = state.Assets.SetItem(dependent, entry with { Dependencies = entry.Dependencies.Add(dependency) }),
        };
    }

    private static CompleteAssetLoad Fail(TaskCompletionSource<Made> waiter, string error) =>
        new(waiter, new Made(Result.Failure(error), 0));
}
