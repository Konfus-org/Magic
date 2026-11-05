using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Magic.Caching;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// The two ends of a load: finished reads become the asset, and what is wanted starts on a worker.
/// </summary>
internal static class AssetLoads
{
    /// <summary>
    /// Turns finished reads into the asset: its waiters are told, what it depends on goes in the database, and one that
    /// had loaded is among what reloaded. A read whose file changed meanwhile is read again instead.
    /// </summary>
    public static (AssetDatabase, LoadedAssets) Land(
        AssetDatabase database,
        LoadedAssets loaded,
        AssetsChangeList changes,
        Caches caches,
        CancellationToken stopped
    )
    {
        if (loaded.Landed.IsEmpty)
            return (database, loaded);

        foreach (ulong id in loaded.Landed)
        {
            // Gone since it finished, with its waiters told as it went.
            if (!loaded.Entries.TryGetValue(id, out AssetEntry? entry) || entry.Landing is not { } made)
                continue;

            if (entry.IsStale)
            {
                loaded = loaded.Want(id);
                continue;
            }

            loaded = Finish(loaded, id, entry, made, changes);
            if (made.Value.Ok && !made.Handles.IsDefault)
            {
                ImmutableArray<ulong> dependencies = [.. made.Handles.Union(entry.MadeFrom)];
                database = database.SetDependencies(id, dependencies);
                if (loaded.Importers.ContainsKey(entry.Type) && database.Index.ById.ContainsKey(id))
                    KeepDependencies(id, made.Version, dependencies, caches, stopped);
            }
        }

        return (database, loaded with { Landed = [] });
    }

    /// <summary>
    /// Starts what is wanted on workers, forgetting what each was made from, as its read records its own. One no file
    /// has fails at once.
    /// </summary>
    public static LoadedAssets Start(
        AssetDatabase database,
        LoadedAssets loaded,
        AssetsChangeList changes,
        CancellationToken stopped
    )
    {
        if (loaded.Wanted.IsEmpty)
            return loaded;

        foreach (ulong id in loaded.Wanted)
        {
            if (!loaded.Entries.TryGetValue(id, out AssetEntry? entry) || entry.Stage != AssetStage.Wanted)
                continue;

            if (!database.Index.ById.TryGetValue(id, out IndexedFile? file))
            {
                Made none = new(Result.Failure($"No asset has the id {id}."), 0);
                loaded = Finish(loaded, id, entry, none, changes);
                continue;
            }

            object? importer = loaded.Importers.GetValueOrDefault(entry.Type);
            Task.Run(() => ReadFileAsync(id, entry, file, importer, changes, stopped), stopped).FireAndForget();
            loaded = loaded with
            {
                Entries = loaded.Entries.SetItem(
                    id,
                    entry with
                    {
                        Stage = AssetStage.Loading,
                        IsStale = false,
                        MadeFrom = [],
                        Folders = [],
                        Progress = null,
                    }
                ),
            };
        }

        return loaded with
        {
            Wanted = [],
        };
    }

    /// <summary>
    /// Takes in what a load of asset <paramref name="id"/> made and tells its waiters: one that failed while what it made
    /// before is kept leaves that in use, and says so.
    /// </summary>
    private static LoadedAssets Finish(
        LoadedAssets loaded,
        ulong id,
        AssetEntry entry,
        Made made,
        AssetsChangeList changes
    )
    {
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

        if (entry.Current is not null && made.Value is { Ok: true, Value: { } asset })
            changes.AddReloaded(id, asset);

        done = done with { Stage = AssetStage.Loaded, Landing = null, Waiting = [] };
        foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
        {
            changes.Answer(waiter, done.Outcome);
        }

        return loaded with
        {
            Entries = loaded.Entries.SetItem(id, done),
        };
    }

    /// <summary>
    /// Keeps what a gem-read asset depends on in the cache, by its file's version, so finding that later doesn't load it.
    /// </summary>
    private static void KeepDependencies(
        ulong id,
        ulong version,
        ImmutableArray<ulong> dependencies,
        Caches caches,
        CancellationToken stopped
    ) =>
        Task.Run(
                () =>
                    caches.WriteAsync(
                        id,
                        version,
                        new AssetDependencies(dependencies),
                        AssetDependencyCache.Instance,
                        stopped
                    ),
                stopped
            )
            .FireAndForget();

    /// <summary>
    /// Reads <paramref name="file"/> and makes its asset, on a worker, and hands what it made to the next run.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An importer that throws on a file fails that file, as content that does not load; the run goes on."
    )]
    private static async Task ReadFileAsync(
        ulong id,
        AssetEntry entry,
        IndexedFile file,
        object? importer,
        AssetsChangeList changes,
        CancellationToken stopped
    )
    {
        Made made;
        try
        {
            made = await entry.Import(id, file, importer, entry.Progress, stopped).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            made = new Made(Result.Failure($"{file.Path} is not a {entry.Type.Name}: its importer threw: {ex}"), 0);
        }

        changes.PushRead(id, made);
    }
}
