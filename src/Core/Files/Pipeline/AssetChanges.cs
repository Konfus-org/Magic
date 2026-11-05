using Magic.Debugging;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Takes what was pushed, and what workers finished, into the database and the loaded assets: the rules of what each
/// change does.
/// </summary>
internal static class AssetChanges
{
    public static (AssetDatabase, LoadedAssets) Take(
        AssetDatabase database,
        LoadedAssets loaded,
        AssetsChangeList changes
    )
    {
        while (changes.TryTakeAsked(out AssetChange? change))
        {
            switch (change)
            {
                case AssetRequested requested:
                    loaded = Request(database, loaded, requested, changes);
                    break;
                case AssetImporterAdded added:
                    loaded = AddImporter(loaded, added);
                    break;
                case AssetImporterRemoved removed:
                    loaded = RemoveImporter(loaded, removed);
                    break;
                case AssetExporterAdded added:
                    loaded = AddExporter(loaded, added);
                    break;
                case AssetExporterRemoved removed:
                    loaded = RemoveExporter(loaded, removed);
                    break;
                case AssetAdded added:
                    loaded = Add(database, loaded, added);
                    break;
                case BuiltInAssetMissing missing:
                    loaded = ReportMissing(loaded, missing);
                    break;
                case AssetFolderListed listed:
                    loaded = RecordFolder(loaded, listed);
                    break;
                case AssetDependenciesFound found:
                    database = database.SetDependencies(found.Id, found.Dependencies);
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

        return (database, loaded);
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
    /// Has <paramref name="added"/>'s importer read its type; a type another reads already stays that one's, and is an
    /// error.
    /// </summary>
    private static LoadedAssets AddImporter(LoadedAssets loaded, AssetImporterAdded added)
    {
        if (loaded.Importers.TryGetValue(added.Type, out object? reads))
        {
            Log.Error(
                $"{added.Importer.GetType().Name} reads {added.Type.Name}s, which {reads.GetType().Name} reads already, "
                    + "so it does not."
            );

            return loaded;
        }

        return loaded with
        {
            Importers = loaded.Importers.Add(added.Type, added.Importer),
        };
    }

    private static LoadedAssets RemoveImporter(LoadedAssets loaded, AssetImporterRemoved removed) =>
        loaded.Importers.TryGetValue(removed.Type, out object? reads) && ReferenceEquals(reads, removed.Importer)
            ? loaded with
            {
                Importers = loaded.Importers.Remove(removed.Type),
            }
            : loaded;

    private static LoadedAssets AddExporter(LoadedAssets loaded, AssetExporterAdded added)
    {
        if (loaded.Exporters.TryGetValue(added.Type, out object? writes))
        {
            Log.Error(
                $"{added.Exporter.GetType().Name} writes {added.Type.Name}s, which {writes.GetType().Name} writes already, "
                    + "so it does not."
            );

            return loaded;
        }

        return loaded with
        {
            Exporters = loaded.Exporters.Add(added.Type, added.Exporter),
        };
    }

    private static LoadedAssets RemoveExporter(LoadedAssets loaded, AssetExporterRemoved removed) =>
        loaded.Exporters.TryGetValue(removed.Type, out object? writes) && ReferenceEquals(writes, removed.Exporter)
            ? loaded with
            {
                Exporters = loaded.Exporters.Remove(removed.Type),
            }
            : loaded;

    /// <summary>
    /// Hands out an asset a gem made, replacing any it added under the id before, a version later; refused when a file
    /// has the id.
    /// </summary>
    private static LoadedAssets Add(AssetDatabase database, LoadedAssets loaded, AssetAdded added)
    {
        if (database.Index.ById.TryGetValue(added.Id, out IndexedFile? file))
        {
            Log.Error(
                $"A {added.Type.Name} a gem made has the id {added.Id}, which {file.Path} has, so it is not added."
            );

            return loaded;
        }

        loaded.Entries.TryGetValue(added.Id, out AssetEntry? before);
        ulong version = (before?.Current?.Version ?? 0) + 1;
        AssetEntry entry = new()
        {
            Type = added.Type,
            Import = added.Import,
            Stage = AssetStage.Landed,
            Waiting = before?.Waiting ?? [],
        };

        return loaded.Land(added.Id, entry, new Made(Result.Success(added.Asset), version));
    }

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
