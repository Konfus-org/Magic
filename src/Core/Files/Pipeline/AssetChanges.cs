using Magic.Debugging;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Takes what was pushed, and what workers finished, into the snapshot: the rules of what each change does.
/// </summary>
internal static class AssetChanges
{
    public static AssetSnapshot Take(AssetSnapshot snapshot, AssetsChangeList changes)
    {
        // The first run comes once every gem is made, so each type's loader is on the list by now.
        snapshot = snapshot with
        {
            IsLoadingEnabled = true,
        };
        while (changes.TryTakeAsked(out AssetChange? change))
        {
            snapshot = change switch
            {
                AssetRequested requested => Request(snapshot, requested, changes),
                AssetLoaderAdded added => AddLoader(snapshot, added),
                AssetLoaderRemoved removed => RemoveLoader(snapshot, removed),
                AssetWriterAdded added => AddWriter(snapshot, added),
                AssetWriterRemoved removed => RemoveWriter(snapshot, removed),
                AssetAdded added => Add(snapshot, added),
                BuiltInAssetMissing missing => ReportMissing(snapshot, missing),
                AssetFolderListed listed => RecordFolder(snapshot, listed),
                AssetDependenciesScanned scanned => snapshot with
                {
                    Scanned = snapshot.Scanned.SetItem(scanned.Id, scanned.Dependencies),
                },
                _ => throw new ArgumentException(
                    $"{change.GetType().Name} is no change the assets take.",
                    nameof(changes)
                ),
            };
        }

        while (changes.TryTakeRead(out AssetFileRead read))
        {
            // Gone while it was read: nobody is told, as its waiters were when it went.
            if (snapshot.Assets.TryGetValue(read.Id, out AssetEntry? entry))
                snapshot = snapshot.Land(read.Id, entry, read.Made);
        }

        return snapshot;
    }

    /// <summary>
    /// Answers <paramref name="requested"/>: with what loaded, by waiting for a load on its way, or by asking for one;
    /// with a failure when it is another type, or, asked for by a loader, when it depends on what asks for it.
    /// </summary>
    private static AssetSnapshot Request(AssetSnapshot snapshot, AssetRequested requested, AssetsChangeList changes)
    {
        ulong id = requested.Id;
        ulong dependent = requested.Dependent;
        if (dependent != 0)
        {
            if (id == dependent || snapshot.GetDependencies(id, Assets.AllLevels).Contains(dependent))
            {
                string name = snapshot.GetName(dependent);
                Fail(changes, requested, $"{name} depends on itself: something it loads as it is made loads it.");

                return snapshot;
            }

            snapshot = RecordDependency(snapshot, dependent, id);
        }

        if (snapshot.Index.ById.TryGetValue(id, out IndexedFile? file) && file.Type != requested.Type.Name)
        {
            Fail(changes, requested, $"{file.Path} is a {file.Type}, so it is not a {requested.Type.Name}.");

            return snapshot;
        }

        if (!snapshot.Assets.TryGetValue(id, out AssetEntry? entry))
        {
            entry = new AssetEntry
            {
                Type = requested.Type,
                Read = requested.Read,
                Stage = AssetStage.Wanted,
                Progress = requested.Progress,
                Waiting = [requested.Waiter],
            };

            return snapshot with
            {
                Assets = snapshot.Assets.SetItem(id, entry),
                Wanted = snapshot.Wanted.Add(id),
            };
        }

        if (entry.Type != requested.Type)
        {
            string name = snapshot.GetName(id);
            Fail(changes, requested, $"{name} is loaded as a {entry.Type.Name}, so it is not a {requested.Type.Name}.");

            return snapshot;
        }

        if (entry.Stage == AssetStage.Loaded)
        {
            changes.Answer(requested.Waiter, entry.Outcome);

            return snapshot;
        }

        return snapshot with
        {
            Assets = snapshot.Assets.SetItem(id, entry with { Waiting = entry.Waiting.Add(requested.Waiter) }),
        };
    }

    /// <summary>
    /// Has <paramref name="added"/>'s loader read its type; a type another reads already stays that one's, and is an
    /// error.
    /// </summary>
    private static AssetSnapshot AddLoader(AssetSnapshot snapshot, AssetLoaderAdded added)
    {
        if (snapshot.Loaders.TryGetValue(added.Type, out object? reads))
        {
            Log.Error(
                $"{added.Loader.GetType().Name} reads {added.Type.Name}s, which {reads.GetType().Name} reads already, "
                    + "so it does not."
            );

            return snapshot;
        }

        return snapshot with
        {
            Loaders = snapshot.Loaders.Add(added.Type, added.Loader),
        };
    }

    private static AssetSnapshot RemoveLoader(AssetSnapshot snapshot, AssetLoaderRemoved removed) =>
        snapshot.Loaders.TryGetValue(removed.Type, out object? reads) && ReferenceEquals(reads, removed.Loader)
            ? snapshot with
            {
                Loaders = snapshot.Loaders.Remove(removed.Type),
            }
            : snapshot;

    private static AssetSnapshot AddWriter(AssetSnapshot snapshot, AssetWriterAdded added)
    {
        if (snapshot.Writers.TryGetValue(added.Type, out object? writes))
        {
            Log.Error(
                $"{added.Writer.GetType().Name} writes {added.Type.Name}s, which {writes.GetType().Name} writes already, "
                    + "so it does not."
            );

            return snapshot;
        }

        return snapshot with
        {
            Writers = snapshot.Writers.Add(added.Type, added.Writer),
        };
    }

    private static AssetSnapshot RemoveWriter(AssetSnapshot snapshot, AssetWriterRemoved removed) =>
        snapshot.Writers.TryGetValue(removed.Type, out object? writes) && ReferenceEquals(writes, removed.Writer)
            ? snapshot with
            {
                Writers = snapshot.Writers.Remove(removed.Type),
            }
            : snapshot;

    /// <summary>
    /// Hands out an asset a gem made, replacing any it added under the id before, a version later; refused when a file
    /// has the id.
    /// </summary>
    private static AssetSnapshot Add(AssetSnapshot snapshot, AssetAdded added)
    {
        if (snapshot.Index.ById.TryGetValue(added.Id, out IndexedFile? file))
        {
            Log.Error(
                $"A {added.Type.Name} a gem made has the id {added.Id}, which {file.Path} has, so it is not added."
            );

            return snapshot;
        }

        snapshot.Assets.TryGetValue(added.Id, out AssetEntry? before);
        ulong version = (before?.Current?.Version ?? 0) + 1;
        AssetEntry entry = new()
        {
            Type = added.Type,
            Read = added.Read,
            Stage = AssetStage.Landed,
            Waiting = before?.Waiting ?? [],
        };

        return snapshot.Land(added.Id, entry, new Made(Result.Success(added.Asset), version));
    }

    /// <summary>
    /// Says a missing default or failure is missing, the first time it is asked for.
    /// </summary>
    private static AssetSnapshot ReportMissing(AssetSnapshot snapshot, BuiltInAssetMissing missing)
    {
        if (snapshot.ReportedMissing.Contains(missing.Key))
            return snapshot;

        Log.Error(missing.Message);

        return snapshot with
        {
            ReportedMissing = snapshot.ReportedMissing.Add(missing.Key),
        };
    }

    private static AssetSnapshot RecordFolder(AssetSnapshot snapshot, AssetFolderListed listed)
    {
        if (!snapshot.Assets.TryGetValue(listed.Id, out AssetEntry? entry) || entry.Folders.Contains(listed.Folder))
            return snapshot;

        return snapshot with
        {
            Assets = snapshot.Assets.SetItem(listed.Id, entry with { Folders = entry.Folders.Add(listed.Folder) }),
        };
    }

    /// <summary>
    /// <paramref name="snapshot"/> with <paramref name="dependency"/> among what asset <paramref name="dependent"/>
    /// depends on.
    /// </summary>
    private static AssetSnapshot RecordDependency(AssetSnapshot snapshot, ulong dependent, ulong dependency)
    {
        if (!snapshot.Assets.TryGetValue(dependent, out AssetEntry? entry) || entry.Dependencies.Contains(dependency))
            return snapshot;

        return snapshot with
        {
            Assets = snapshot.Assets.SetItem(
                dependent,
                entry with
                {
                    Dependencies = entry.Dependencies.Add(dependency),
                }
            ),
        };
    }

    private static void Fail(AssetsChangeList changes, AssetRequested requested, string error) =>
        changes.Answer(requested.Waiter, new Made(Result.Failure(error), 0));
}
