using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Takes in what the watches see: once they have seen nothing for <see cref="_settleTime"/>, a worker reads the asset
/// directories again. Then each asset loaded whose file changed, is another file now, lists a folder that changed, or
/// depends on one of those, and so on
/// out, loads again, each after its dependencies, as a load waits for those on their way; one whose file is gone is let go.
/// </summary>
internal static class AssetReindex
{
    /// <summary>
    /// How long the files must have been left alone before their changes are taken in: an editor saving a file, or a
    /// build copying a folder, writes several times in a row.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromMilliseconds(300);

    public static AssetSnapshot Take(AssetSnapshot snapshot, AssetsChangeList state, Assets assets)
    {
        while (state.TryTakeReindexed(out AssetDirectoriesReindexed reindexed))
        {
            snapshot = snapshot with { IsIndexing = false };
            if (reindexed.Index is { } index)
            {
                snapshot = Reindex(snapshot, index, reindexed.Touched, state);
            }
        }

        while (state.TryTakeTouched(out string? path))
        {
            snapshot = snapshot with
            {
                Touched = snapshot.Touched.Add(path),
                TouchedTimestamp = Stopwatch.GetTimestamp(),
            };
        }

        if (
            !snapshot.Touched.IsEmpty
            && !snapshot.IsIndexing
            && Stopwatch.GetElapsedTime(snapshot.TouchedTimestamp) >= _settleTime
        )
        {
            ImmutableHashSet<string> touched = snapshot.Touched;
            ImmutableArray<string> directories = snapshot.Directories;
            snapshot = snapshot with { Touched = touched.Clear(), IsIndexing = true };
            Task.Run(() => ReadDirectories(directories, touched, state, assets), assets.Stopped).FireAndForget();
        }

        return snapshot;
    }

    private static AssetSnapshot Reindex(
        AssetSnapshot snapshot,
        AssetIndex now,
        ImmutableHashSet<string> touched,
        AssetsChangeList changes
    )
    {
        AssetIndex before = snapshot.Index;
        HashSet<ulong> changed = AssetIndex.Diff(before, now, touched);
        changed.UnionWith(snapshot.GetListersOf(touched));
        HashSet<ulong> reload = [.. changed, .. snapshot.GetDependents(changed, Assets.AllLevels)];
        snapshot = snapshot with { Index = now, Scanned = snapshot.Scanned.RemoveRange(changed) };
        int reloading = 0;
        foreach (ulong id in reload)
        {
            if (!snapshot.Assets.TryGetValue(id, out AssetEntry? entry))
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

                snapshot = snapshot with { Assets = snapshot.Assets.Remove(id) };
                continue;
            }

            if (entry.Stage == AssetStage.Wanted)
                continue;

            reloading++;
            snapshot =
                entry.Stage == AssetStage.Loaded
                    ? snapshot.Want(id)
                    : snapshot with
                    {
                        Assets = snapshot.Assets.SetItem(id, entry with { IsStale = true }),
                    };
        }

        if (reloading > 0)
            Log.Info($"Reloading {reloading} assets.");

        return snapshot;
    }

    /// <summary>
    /// Reads the asset directories again, on a worker, and pushes what it found. Whatever goes wrong is logged, and the next
    /// change is still taken in.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever goes wrong taking in one batch is logged, and the next is still taken in."
    )]
    private static void ReadDirectories(
        ImmutableArray<string> directories,
        ImmutableHashSet<string> touched,
        AssetsChangeList changes,
        Assets assets
    )
    {
        try
        {
            changes.PushReindexed(AssetIndex.Create(assets.Files, directories, assets.BuiltIn), touched);
        }
        catch (Exception ex)
        {
            Log.Error($"The asset files' changes could not be taken in: {ex}");
            changes.PushReindexed(null, touched);
        }
    }
}
