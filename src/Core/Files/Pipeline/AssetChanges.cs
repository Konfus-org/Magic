using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Takes what was pushed, and what workers finished, into the loaded assets: the rules of what each change does.
/// </summary>
internal static class AssetChanges
{
    public static LoadedAssets Take(AssetDatabase database, LoadedAssets loaded, AssetsChangeList changes)
    {
        while (changes.TryTakeAsked(out AssetChange? change))
        {
            switch (change)
            {
                case AssetRequested requested:
                    loaded = Request(database, loaded, requested, changes);
                    break;
                case AssetImporterAdded added:
                    loaded = loaded with { Importers = Claim(loaded.Importers, added.Type, added.Importer, "reads") };
                    break;
                case AssetImporterRemoved removed:
                    loaded = loaded with { Importers = Release(loaded.Importers, removed.Type, removed.Importer) };
                    break;
                case AssetExporterAdded added:
                    loaded = loaded with { Exporters = Claim(loaded.Exporters, added.Type, added.Exporter, "writes") };
                    break;
                case AssetExporterRemoved removed:
                    loaded = loaded with { Exporters = Release(loaded.Exporters, removed.Type, removed.Exporter) };
                    break;
                case BuiltInAssetMissing missing:
                    loaded = ReportMissing(loaded, missing);
                    break;
                case AssetFolderListed listed:
                    loaded = RecordFolder(loaded, listed);
                    break;
                default:
                    throw new ArgumentException(
                        $"{change.GetType().Name} is no change the assets take.",
                        nameof(changes)
                    );
            }
        }

        while (changes.TryTakeRead(out AssetFileRead read))
        {
            // Gone while it was read: nobody is told, as its waiters were when it went.
            if (loaded.Entries.TryGetValue(read.Id, out AssetEntry? entry))
                loaded = loaded.Land(read.Id, entry, read.Made);
        }

        return loaded;
    }

    /// <summary>
    /// Answers <paramref name="requested"/>: with what loaded, by waiting for a load on its way, or by asking for one;
    /// with a failure when it is another type, or, asked for by an importer, when it depends on what asks for it.
    /// </summary>
    private static LoadedAssets Request(
        AssetDatabase database,
        LoadedAssets loaded,
        AssetRequested requested,
        AssetsChangeList changes
    )
    {
        ulong id = requested.Id;
        ulong dependent = requested.Dependent;
        if (dependent != 0)
        {
            if (id == dependent || loaded.IsMadeFrom(id, dependent))
            {
                string name = database.GetName(dependent);
                Fail(changes, requested, $"{name} depends on itself: something it loads as it is made loads it.");

                return loaded;
            }

            loaded = RecordMadeFrom(loaded, dependent, id);
        }

        if (database.Index.ById.TryGetValue(id, out IndexedFile? file) && file.Type != requested.Type.Name)
        {
            Fail(changes, requested, $"{file.Path} is a {file.Type}, so it is not a {requested.Type.Name}.");

            return loaded;
        }

        if (!loaded.Entries.TryGetValue(id, out AssetEntry? entry))
        {
            entry = new AssetEntry
            {
                Type = requested.Type,
                Import = requested.Import,
                Stage = AssetStage.Wanted,
                Progress = requested.Progress,
                Waiting = [requested.Waiter],
            };

            return loaded with
            {
                Entries = loaded.Entries.SetItem(id, entry),
                Wanted = loaded.Wanted.Add(id),
            };
        }

        if (entry.Type != requested.Type)
        {
            string name = database.GetName(id);
            Fail(changes, requested, $"{name} is loaded as a {entry.Type.Name}, so it is not a {requested.Type.Name}.");

            return loaded;
        }

        if (entry.Stage == AssetStage.Loaded)
        {
            changes.Answer(requested.Waiter, entry.Outcome);

            return loaded;
        }

        return loaded with
        {
            Entries = loaded.Entries.SetItem(id, entry with { Waiting = entry.Waiting.Add(requested.Waiter) }),
        };
    }

    /// <summary>
    /// <paramref name="gems"/> with <paramref name="gem"/> as the one that <paramref name="verb"/> <paramref name="type"/>;
    /// a type another gem has already stays that one's, and is an error.
    /// </summary>
    private static ImmutableDictionary<Type, object> Claim(
        ImmutableDictionary<Type, object> gems,
        Type type,
        object gem,
        string verb
    )
    {
        if (!gems.TryGetValue(type, out object? other))
            return gems.Add(type, gem);

        Log.Error(
            $"{gem.GetType().Name} {verb} {type.Name}s, which {other.GetType().Name} {verb} already, so it does not."
        );

        return gems;
    }

    /// <summary>
    /// <paramref name="gems"/> without <paramref name="gem"/> as the one that has <paramref name="type"/>, if it is.
    /// </summary>
    private static ImmutableDictionary<Type, object> Release(
        ImmutableDictionary<Type, object> gems,
        Type type,
        object gem
    ) => gems.TryGetValue(type, out object? other) && ReferenceEquals(other, gem) ? gems.Remove(type) : gems;

    /// <summary>
    /// Says a missing default or failure is missing, the first time it is asked for.
    /// </summary>
    private static LoadedAssets ReportMissing(LoadedAssets loaded, BuiltInAssetMissing missing)
    {
        if (loaded.ReportedMissing.Contains(missing.Key))
            return loaded;

        Log.Error(missing.Message);

        return loaded with
        {
            ReportedMissing = loaded.ReportedMissing.Add(missing.Key),
        };
    }

    private static LoadedAssets RecordFolder(LoadedAssets loaded, AssetFolderListed listed)
    {
        if (!loaded.Entries.TryGetValue(listed.Id, out AssetEntry? entry) || entry.Folders.Contains(listed.Folder))
            return loaded;

        return loaded with
        {
            Entries = loaded.Entries.SetItem(listed.Id, entry with { Folders = entry.Folders.Add(listed.Folder) }),
        };
    }

    /// <summary>
    /// <paramref name="loaded"/> with <paramref name="source"/> among what asset <paramref name="dependent"/> is made
    /// from.
    /// </summary>
    private static LoadedAssets RecordMadeFrom(LoadedAssets loaded, ulong dependent, ulong source)
    {
        if (!loaded.Entries.TryGetValue(dependent, out AssetEntry? entry) || entry.MadeFrom.Contains(source))
            return loaded;

        return loaded with
        {
            Entries = loaded.Entries.SetItem(dependent, entry with { MadeFrom = entry.MadeFrom.Add(source) }),
        };
    }

    private static void Fail(AssetsChangeList changes, AssetRequested requested, string error) =>
        changes.Answer(requested.Waiter, new Made(Result.Failure(error), 0));
}
