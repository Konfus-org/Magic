using System.Diagnostics.CodeAnalysis;
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
    /// Turns finished reads into the asset: its waiters are told, and one that had loaded is among what reloaded. A read
    /// whose file changed meanwhile is read again instead.
    /// </summary>
    public static AssetSnapshot Land(AssetSnapshot snapshot, AssetsChangeList changes)
    {
        if (snapshot.Landed.IsEmpty)
            return snapshot;

        foreach (ulong id in snapshot.Landed)
        {
            // Gone since it finished, with its waiters told as it went.
            if (!snapshot.Assets.TryGetValue(id, out AssetEntry? entry) || entry.Landing is not { } made)
                continue;

            snapshot = entry.IsStale ? snapshot.Want(id) : Finish(snapshot, id, entry, made, changes);
        }

        return snapshot with
        {
            Landed = [],
        };
    }

    /// <summary>
    /// Starts what is wanted on workers, forgetting what each depended on, as its read records its own. One no file has
    /// fails at once.
    /// </summary>
    public static AssetSnapshot Start(AssetSnapshot snapshot, AssetsChangeList changes, CancellationToken stopped)
    {
        if (snapshot.Wanted.IsEmpty)
            return snapshot;

        foreach (ulong id in snapshot.Wanted)
        {
            if (snapshot.Assets.TryGetValue(id, out AssetEntry? entry) && entry.Stage == AssetStage.Wanted)
                snapshot = StartOne(snapshot, id, entry, changes, stopped);
        }

        return snapshot with
        {
            Wanted = [],
        };
    }

    /// <summary>
    /// Takes in what a load of asset <paramref name="id"/> made and tells its waiters: one that failed while what it made
    /// before is kept leaves that in use, and says so.
    /// </summary>
    private static AssetSnapshot Finish(
        AssetSnapshot snapshot,
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

        return snapshot with
        {
            Assets = snapshot.Assets.SetItem(id, done),
            Scanned =
                made.Value.Ok && !made.Handles.IsDefault
                    ? snapshot.Scanned.SetItem(id, made.Handles)
                    : snapshot.Scanned,
        };
    }

    /// <summary>
    /// Reads <paramref name="file"/> and makes its asset, on a worker, and hands what it made to the next run.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A loader that throws on a file fails that file, as content that does not load; the run goes on."
    )]
    private static async Task ReadFileAsync(
        ulong id,
        AssetEntry entry,
        IndexedFile file,
        object? loader,
        AssetsChangeList changes,
        CancellationToken stopped
    )
    {
        Made made;
        try
        {
            made = await entry.Read(id, file, loader, entry.Progress, stopped).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            made = new Made(Result.Failure($"{file.Path} is not a {entry.Type.Name}: its loader threw: {ex}"), 0);
        }

        changes.PushRead(id, made);
    }

    private static AssetSnapshot StartOne(
        AssetSnapshot snapshot,
        ulong id,
        AssetEntry entry,
        AssetsChangeList changes,
        CancellationToken stopped
    )
    {
        if (!snapshot.Index.ById.TryGetValue(id, out IndexedFile? file))
        {
            Made none = new(Result.Failure($"No asset has the id {id}."), 0);

            return Finish(snapshot, id, entry, none, changes);
        }

        object? loader = snapshot.Loaders.GetValueOrDefault(entry.Type);
        Task.Run(() => ReadFileAsync(id, entry, file, loader, changes, stopped), stopped).FireAndForget();

        return snapshot with
        {
            Assets = snapshot.Assets.SetItem(
                id,
                entry with
                {
                    Stage = AssetStage.Loading,
                    IsStale = false,
                    Dependencies = [],
                    Folders = [],
                    Progress = null,
                }
            ),
        };
    }
}
