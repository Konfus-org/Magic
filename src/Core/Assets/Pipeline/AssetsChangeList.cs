using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// An asset that had loaded, as it is now.
/// </summary>
public readonly record struct AssetLoaded(ulong Id, object Asset);

/// <summary>
/// What was asked of the assets and what came of it: the state of the pipeline that changes them, which runs between frames
/// when there is any. Core's own act on what was asked, through <see cref="AssetDatabase"/>'s calls, from any thread. A gem
/// that joins by implementing <see cref="Pipelines.IPipeline{TState}"/> of it reads <see cref="Reloaded"/> and
/// <see cref="IsReindexed"/>.
/// </summary>
public sealed class AssetsChangeList
{
    private readonly Channel<AssetChange> _asked = Channel.CreateUnbounded<AssetChange>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>
    /// What workers finished reading.
    /// </summary>
    private readonly Channel<AssetFileRead> _read = Channel.CreateUnbounded<AssetFileRead>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>
    /// The paths the watches saw change, from their asset directories.
    /// </summary>
    private readonly Channel<string> _touched = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>
    /// What came of a worker reading the asset directories again.
    /// </summary>
    private readonly Channel<AssetDirectoriesReindexed> _reindexed = Channel.CreateUnbounded<AssetDirectoriesReindexed>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    private readonly List<AssetLoaded> _reloaded = [];

    /// <summary>
    /// What this run has for callers waiting, told once the snapshot they will read holds it.
    /// </summary>
    private readonly List<(TaskCompletionSource<Made> Waiter, Made Made)> _answers = [];

    internal AssetsChangeList() { }

    /// <summary>
    /// The assets that had loaded and loaded again in this run. Good for this run, on the main thread.
    /// </summary>
    public ReadOnlySpan<AssetLoaded> Reloaded => CollectionsMarshal.AsSpan(_reloaded);

    /// <summary>
    /// Whether the asset directories were read again in this run, so files may have come, gone or moved.
    /// </summary>
    public bool IsReindexed { get; private set; }

    internal bool HasChanges =>
        _asked.Reader.TryPeek(out _)
        || _read.Reader.TryPeek(out _)
        || _touched.Reader.TryPeek(out _)
        || _reindexed.Reader.TryPeek(out _);

    /// <summary>
    /// Asks for asset <paramref name="id"/> as <paramref name="type"/>, by asset <paramref name="dependent"/>'s loader when
    /// that is not 0: what its load makes.
    /// </summary>
    internal Task<Made> RequestAsync(
        ulong id,
        ulong dependent,
        Type type,
        AssetReader read,
        IProgress<LoadProgress>? progress
    )
    {
        TaskCompletionSource<Made> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Push(new AssetRequested(id, type, read, progress, waiter, dependent));

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
    /// A watch saw <paramref name="path"/>, from its asset directory, change.
    /// </summary>
    internal void PushTouched(string path) => _touched.Writer.TryWrite(path);

    /// <summary>
    /// A worker read the asset directories again, as <paramref name="index"/>, none when that failed, after the watches saw
    /// <paramref name="touched"/>.
    /// </summary>
    internal void PushReindexed(AssetIndex? index, ImmutableHashSet<string> touched) =>
        _reindexed.Writer.TryWrite(new AssetDirectoriesReindexed(index, touched));

    internal bool TryTakeAsked([NotNullWhen(true)] out AssetChange? change) => _asked.Reader.TryRead(out change);

    internal bool TryTakeRead(out AssetFileRead read) => _read.Reader.TryRead(out read);

    internal bool TryTakeTouched([NotNullWhen(true)] out string? path) => _touched.Reader.TryRead(out path);

    internal bool TryTakeReindexed(out AssetDirectoriesReindexed reindexed) => _reindexed.Reader.TryRead(out reindexed);

    internal void MarkReindexed() => IsReindexed = true;

    internal void AddReloaded(ulong id, object asset) => _reloaded.Add(new AssetLoaded(id, asset));

    /// <summary>
    /// Keeps what <paramref name="waiter"/> gets until <see cref="TellWaiters"/>.
    /// </summary>
    internal void Answer(TaskCompletionSource<Made> waiter, Made made) => _answers.Add((waiter, made));

    /// <summary>
    /// Tells the waiters what they get. The snapshot they will read has it by now.
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
    /// Forgets what came of the run that just ended.
    /// </summary>
    internal void EndRun()
    {
        _reloaded.Clear();
        IsReindexed = false;
    }

    /// <summary>
    /// Takes no more, and ends the wait of whoever asked for what is still on the list.
    /// </summary>
    internal void Close(CancellationToken stopped)
    {
        _asked.Writer.TryComplete();
        _read.Writer.TryComplete();
        _touched.Writer.TryComplete();
        _reindexed.Writer.TryComplete();
        while (_asked.Reader.TryRead(out AssetChange? change))
        {
            if (change is AssetRequested requested)
                requested.Waiter.TrySetCanceled(stopped);
        }
    }
}

/// <summary>
/// Something asked of the assets, kept in the order it was.
/// </summary>
internal abstract record AssetChange;

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
) : AssetChange;

internal sealed record AssetLoaderAdded(Type Type, object Loader) : AssetChange;

internal sealed record AssetLoaderRemoved(Type Type, object Loader) : AssetChange;

internal sealed record AssetWriterAdded(Type Type, object Writer) : AssetChange;

internal sealed record AssetWriterRemoved(Type Type, object Writer) : AssetChange;

/// <summary>
/// A gem made <paramref name="Asset"/> and hands it out as asset <paramref name="Id"/>.
/// </summary>
internal sealed record AssetAdded(ulong Id, Type Type, AssetReader Read, object Asset) : AssetChange;

/// <summary>
/// A built-in default or failure, <paramref name="Key"/>, was asked for and is missing, as <paramref name="Message"/>
/// says.
/// </summary>
internal sealed record BuiltInAssetMissing(string Key, string Message) : AssetChange;

/// <summary>
/// Asset <paramref name="Id"/>'s loader read what is in <paramref name="Folder"/>, so it is made again when that changes.
/// </summary>
internal sealed record AssetFolderListed(ulong Id, string Folder) : AssetChange;

/// <summary>
/// A scan of asset <paramref name="Id"/>'s file found what it depends on, without loading them.
/// </summary>
internal sealed record AssetDependenciesScanned(ulong Id, ImmutableArray<ulong> Dependencies) : AssetChange;

/// <summary>
/// A worker finished reading asset <paramref name="Id"/>'s file, which made <paramref name="Made"/>.
/// </summary>
internal readonly record struct AssetFileRead(ulong Id, Made Made);

/// <summary>
/// The asset directories as they were read again, none when that failed, and the paths the watches saw that led to it.
/// </summary>
internal readonly record struct AssetDirectoriesReindexed(AssetIndex? Index, ImmutableHashSet<string> Touched);
