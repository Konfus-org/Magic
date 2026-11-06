using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Takes in the asset directories as they were read again (<see cref="AssetWatches"/>): each asset loaded whose file
/// changed, is another file now, lists a folder that changed, or was made from one of those, and so on out, loads again,
/// each after what it was made from, as a load waits for those on their way; one whose file is gone is let go.
/// </summary>
internal static class AssetReindex
{
    public static (AssetDatabase, LoadedAssets) Take(
        AssetDatabase database,
        LoadedAssets loaded,
        AssetWatches watches,
        AssetsChangeList changes,
        string builtIn,
        CancellationToken stopped
    )
    {
        while (watches.TryTakeRead(out AssetDirectoriesReindexed read))
        {
            (database, loaded) = Reindex(database, loaded, read.Index, read.Touched, changes);
        }

        watches.ReadOnceSettled(database.Directories, builtIn, stopped);

        return (database, loaded);
    }

    private static (AssetDatabase, LoadedAssets) Reindex(
        AssetDatabase database,
        LoadedAssets loaded,
        AssetIndex now,
        ImmutableHashSet<string> touched,
        AssetsChangeList changes
    )
    {
        AssetIndex before = database.Index;
        HashSet<ulong> changed = AssetIndex.Diff(before, now, touched);
        changed.UnionWith(loaded.GetListersOf(touched));
        HashSet<ulong> reload = [.. changed, .. loaded.GetMadeFromThese(changed)];
        database = database with { Index = now };
        foreach (ulong id in changed)
        {
            database = database.RemoveDependencies(id);
        }

        int reloading = 0;
        foreach (ulong id in reload)
        {
            if (!loaded.Entries.TryGetValue(id, out AssetEntry? entry))
                continue;

            if (!now.ById.ContainsKey(id))
            {
                // One a gem added has no file, and stays.
                if (!before.ById.TryGetValue(id, out IndexedFile? gone))
                    continue;

                foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
                {
                    changes.Answer(waiter, new Made(Result.Failure($"{gone.Path} is gone."), 0));
                }

                loaded = loaded with { Entries = loaded.Entries.Remove(id) };
                continue;
            }

            if (entry.Stage == AssetStage.Wanted)
                continue;

            reloading++;
            loaded =
                entry.Stage == AssetStage.Loaded
                    ? loaded.Want(id)
                    : loaded with
                    {
                        Entries = loaded.Entries.SetItem(id, entry with { IsStale = true }),
                    };
        }

        if (reloading > 0)
            Log.Info($"Reloading {reloading} assets.");

        return (database, loaded);
    }
}
