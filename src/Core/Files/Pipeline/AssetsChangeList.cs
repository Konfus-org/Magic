using System.Collections.Immutable;
using System.Threading.Channels;
using Magic.Debugging;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// What was asked of the assets and what came of it, taken into the database and the loaded assets between frames when
/// there is any: the rules of what each change does. Core's own act on what was asked, through <see cref="Assets"/>'s
/// calls, from any thread.
/// </summary>
internal sealed class AssetsChangeList
{
    // Not single-reader: HasChanges peeks from any thread while the main thread takes.
    private readonly Channel<AssetChange> _asked = Channel.CreateUnbounded<AssetChange>();

    /// <summary>
    /// What workers finished reading.
    /// </summary>
    private readonly Channel<AssetFileRead> _read = Channel.CreateUnbounded<AssetFileRead>();

    /// <summary>
    /// What this run has for callers waiting, told once what they will read holds it.
    /// </summary>
    private readonly List<(TaskCompletionSource<Made> Waiter, Made Made)> _answers = [];

    internal AssetsChangeList() { }

    internal bool HasChanges => _asked.Reader.TryPeek(out _) || _read.Reader.TryPeek(out _);

    /// <summary>
    /// Asks for asset <paramref name="id"/> as <paramref name="type"/>, by asset <paramref name="dependent"/>'s importer
    /// when that is not 0: what its load makes.
    /// </summary>
    internal Task<Made> RequestAsync(
        ulong id,
        ulong dependent,
        Type type,
        AssetImportRun import,
        IProgress<LoadProgress>? progress
    )
    {
        TaskCompletionSource<Made> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Push(new AssetRequested(id, type, import, progress, waiter, dependent));

        return waiter.Task;
    }

    /// <summary>
    /// Hands <paramref name="change"/> to the next run; once the assets are disposed, there is none.
    /// </summary>
    internal void Push(AssetChange change) => ObjectDisposedException.ThrowIf(!_asked.Writer.TryWrite(change), this);

    /// <summary>
    /// A worker finished reading asset <paramref name="id"/>'s file, which made <paramref name="made"/>.
    /// </summary>
    internal void PushRead(ulong id, Made made) => _read.Writer.TryWrite(new AssetFileRead(id, made));

    /// <summary>
    /// Takes what was pushed, and what workers finished, into <paramref name="database"/> and <paramref name="loaded"/>.
    /// Main thread.
    /// </summary>
    internal (AssetDatabase, LoadedAssets) Take(AssetDatabase database, LoadedAssets loaded)
    {
        while (_asked.Reader.TryRead(out AssetChange? change))
        {
            switch (change)
            {
                case AssetRequested requested:
                    loaded = Request(database, loaded, requested);
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
                case AssetDependenciesFound found:
                    database = database.SetDependencies(found.Id, found.Dependencies);
                    break;
                default:
                    throw new InvalidOperationException($"{change.GetType().Name} is no change the assets take.");
            }
        }

        while (_read.Reader.TryRead(out AssetFileRead read))
        {
            // Gone while it was read: nobody is told, as its waiters were when it went.
            if (loaded.Entries.TryGetValue(read.Id, out AssetEntry? entry))
                loaded = loaded.Land(read.Id, entry, read.Made);
        }

        return (database, loaded);
    }

    /// <summary>
    /// Keeps what <paramref name="waiter"/> gets until <see cref="TellWaiters"/>.
    /// </summary>
    internal void Answer(TaskCompletionSource<Made> waiter, Made made) => _answers.Add((waiter, made));

    /// <summary>
    /// Tells the waiters what they get. What they will read has it by now.
    /// </summary>
    internal void TellWaiters()
    {
        foreach ((TaskCompletionSource<Made> waiter, Made made) in _answers)
        {
            waiter.TrySetResult(made);
        }

        _answers.Clear();
    }

    /// <summary>
    /// Takes no more, and ends the wait of whoever asked for what is still on the list.
    /// </summary>
    internal void Close(CancellationToken stopped)
    {
        _asked.Writer.TryComplete();
        _read.Writer.TryComplete();
        while (_asked.Reader.TryRead(out AssetChange? change))
        {
            if (change is AssetRequested requested)
                requested.Waiter.TrySetCanceled(stopped);
        }
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

    /// <summary>
    /// Answers <paramref name="requested"/>: with what loaded, by waiting for a load on its way, or by asking for one;
    /// with a failure when it is another type, or, asked for by an importer, when it depends on what asks for it.
    /// </summary>
    private LoadedAssets Request(AssetDatabase database, LoadedAssets loaded, AssetRequested requested)
    {
        ulong id = requested.Id;
        ulong dependent = requested.Dependent;
        if (dependent != 0)
        {
            if (id == dependent || loaded.IsMadeFrom(id, dependent))
            {
                string name = database.GetName(dependent);
                Fail(requested, $"{name} depends on itself: something it loads as it is made loads it.");

                return loaded;
            }

            loaded = RecordMadeFrom(loaded, dependent, id);
        }

        if (database.Index.ById.TryGetValue(id, out IndexedFile? file) && file.Type != requested.Type.Name)
        {
            Fail(requested, $"{file.Path} is a {file.Type}, so it is not a {requested.Type.Name}.");

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
            Fail(requested, $"{name} is loaded as a {entry.Type.Name}, so it is not a {requested.Type.Name}.");

            return loaded;
        }

        if (entry.Stage == AssetStage.Loaded)
        {
            Answer(requested.Waiter, entry.Outcome);

            return loaded;
        }

        return loaded with
        {
            Entries = loaded.Entries.SetItem(id, entry with { Waiting = entry.Waiting.Add(requested.Waiter) }),
        };
    }

    private void Fail(AssetRequested requested, string error) =>
        Answer(requested.Waiter, new Made(Result.Failure(error), 0));
}

/// <summary>
/// Something asked of the assets, kept in the order it was.
/// </summary>
internal abstract record AssetChange;

/// <summary>
/// A caller asked for asset <paramref name="Id"/> as <paramref name="Type"/>, to be told on
/// <paramref name="Waiter"/>; an importer did, as what asset <paramref name="Dependent"/> is made from, when it is not 0.
/// </summary>
internal sealed record AssetRequested(
    ulong Id,
    Type Type,
    AssetImportRun Import,
    IProgress<LoadProgress>? Progress,
    TaskCompletionSource<Made> Waiter,
    ulong Dependent
) : AssetChange;

internal sealed record AssetImporterAdded(Type Type, object Importer) : AssetChange;

internal sealed record AssetImporterRemoved(Type Type, object Importer) : AssetChange;

internal sealed record AssetExporterAdded(Type Type, object Exporter) : AssetChange;

internal sealed record AssetExporterRemoved(Type Type, object Exporter) : AssetChange;

/// <summary>
/// A built-in default or failure, <paramref name="Key"/>, was asked for and is missing, as <paramref name="Message"/>
/// says.
/// </summary>
internal sealed record BuiltInAssetMissing(string Key, string Message) : AssetChange;

/// <summary>
/// Asset <paramref name="Id"/>'s importer read what is in <paramref name="Folder"/>, so it is made again when that
/// changes.
/// </summary>
internal sealed record AssetFolderListed(ulong Id, string Folder) : AssetChange;

/// <summary>
/// What asset <paramref name="Id"/> depends on was found without loading it.
/// </summary>
internal sealed record AssetDependenciesFound(ulong Id, ImmutableArray<ulong> Dependencies) : AssetChange;

/// <summary>
/// A worker finished reading asset <paramref name="Id"/>'s file, which made <paramref name="Made"/>.
/// </summary>
internal readonly record struct AssetFileRead(ulong Id, Made Made);
