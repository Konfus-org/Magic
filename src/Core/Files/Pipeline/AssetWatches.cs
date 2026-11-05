using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// The asset directories' file watches, and reading the directories again once what they saw has settled.
/// </summary>
internal sealed class AssetWatches(IFileSystem files) : IDisposable
{
    /// <summary>
    /// How long the files must have been left alone before their changes are taken in: an editor saving a file, or a
    /// build copying a folder, writes several times in a row.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromMilliseconds(300);

    private readonly Subscriptions _watchers = [];

    /// <summary>
    /// The paths the watches saw change, from their asset directories.
    /// </summary>
    private readonly Channel<string> _seen = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>
    /// What came of a worker reading the asset directories again.
    /// </summary>
    private readonly Channel<AssetDirectoriesReindexed> _read = Channel.CreateUnbounded<AssetDirectoriesReindexed>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>
    /// The paths the watches saw change, not yet settled. Main thread.
    /// </summary>
    private ImmutableHashSet<string> _touched = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the watches last saw a change (<see cref="Stopwatch.GetTimestamp"/>). Main thread.
    /// </summary>
    private long _touchedTimestamp;

    /// <summary>
    /// Whether a worker is reading the asset directories again, so another read waits for it. Main thread.
    /// </summary>
    private bool _isReading;

    /// <summary>
    /// Whether anything was seen or read that has not been taken in.
    /// </summary>
    public bool HasWork => !_touched.IsEmpty || _seen.Reader.TryPeek(out _) || _read.Reader.TryPeek(out _);

    public void Dispose()
    {
        _watchers.Dispose();
        _seen.Writer.TryComplete();
        _read.Writer.TryComplete();
    }

    /// <summary>
    /// Watches <paramref name="directory"/>. Failing to is a warning: a change there is not taken in.
    /// </summary>
    public void Add(string directory)
    {
        Result<IFileWatcher> watched = files.Watch(directory);
        if (!watched.Ok)
        {
            Log.Warn(
                $"The assets under {directory} are not watched, so a change there is not taken in: {watched.Error}"
            );

            return;
        }

        IFileWatcher watcher = watched.Value;
        watcher.Created += (_, e) => Touch(directory, e.FullPath);
        watcher.Changed += (_, e) => Touch(directory, e.FullPath);
        watcher.Deleted += (_, e) => Touch(directory, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Touch(directory, e.OldFullPath);
            Touch(directory, e.FullPath);
        };
        _watchers.Add(new Subscription(watcher.Dispose));
    }

    /// <summary>
    /// Takes the directories as a worker read them again, none when that failed, with the paths that led to it. Main
    /// thread.
    /// </summary>
    public bool TryTakeRead(out AssetDirectoriesReindexed read)
    {
        if (!_read.Reader.TryRead(out read))
            return false;

        _isReading = false;

        return true;
    }

    /// <summary>
    /// Takes in what the watches saw, and has a worker read <paramref name="directories"/> again once they have seen
    /// nothing for <see cref="_settleTime"/>. Main thread.
    /// </summary>
    public void ReadOnceSettled(ImmutableArray<string> directories, string builtIn, CancellationToken stopped)
    {
        while (_seen.Reader.TryRead(out string? path))
        {
            _touched = _touched.Add(path);
            _touchedTimestamp = Stopwatch.GetTimestamp();
        }

        if (_touched.IsEmpty || _isReading || Stopwatch.GetElapsedTime(_touchedTimestamp) < _settleTime)
            return;

        ImmutableHashSet<string> touched = _touched;
        _touched = touched.Clear();
        _isReading = true;
        Task.Run(() => ReadDirectories(directories, builtIn, touched), stopped).FireAndForget();
    }

    /// <summary>
    /// Reads the asset directories again, on a worker, and hands over what it found. Whatever goes wrong is logged, and
    /// the next change is still taken in.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever goes wrong taking in one batch is logged, and the next is still taken in."
    )]
    private void ReadDirectories(ImmutableArray<string> directories, string builtIn, ImmutableHashSet<string> touched)
    {
        try
        {
            _read.Writer.TryWrite(
                new AssetDirectoriesReindexed(AssetIndex.Create(files, directories, builtIn), touched)
            );
        }
        catch (Exception ex)
        {
            Log.Error($"The asset files' changes could not be taken in: {ex}");
            _read.Writer.TryWrite(new AssetDirectoriesReindexed(null, touched));
        }
    }

    /// <summary>
    /// Keeps what a watch saw at <paramref name="fullPath"/> under <paramref name="directory"/>, as a path from the asset
    /// directory, a sidecar's change being its file's.
    /// </summary>
    private void Touch(string directory, string fullPath)
    {
        if (fullPath.Length <= directory.Length)
            return;

        string path = fullPath[(directory.Length + 1)..].Replace('\\', '/');
        if (path.EndsWith(AssetIndex.Sidecar, StringComparison.OrdinalIgnoreCase))
            path = path[..^AssetIndex.Sidecar.Length];

        _seen.Writer.TryWrite(path);
    }
}

/// <summary>
/// The asset directories as they were read again, none when that failed, and the paths the watches saw that led to it.
/// </summary>
internal readonly record struct AssetDirectoriesReindexed(AssetIndex? Index, ImmutableHashSet<string> Touched);
