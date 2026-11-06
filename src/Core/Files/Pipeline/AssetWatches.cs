using System.Collections.Immutable;
using System.Diagnostics;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// The asset directories' file watches, and reading the directories again for <paramref name="assets"/> once what they
/// saw has settled.
/// </summary>
internal sealed class AssetWatches(IFileSystem files, Assets assets) : IDisposable
{
    /// <summary>
    /// How long the files must have been left alone before their changes are taken in: an editor saving a file, or a
    /// build copying a folder, writes several times in a row.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Held for what the watches saw and the worker on it, which a watch's thread and the worker share.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly Subscriptions _watchers = [];

    /// <summary>
    /// The paths the watches saw change, from their asset directories, not yet read. Under <see cref="_gate"/>.
    /// </summary>
    private ImmutableHashSet<string> _touched = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the watches last saw a change (<see cref="Stopwatch.GetTimestamp"/>). Under <see cref="_gate"/>.
    /// </summary>
    private long _touchedTimestamp;

    /// <summary>
    /// The worker waiting for the files to settle and reading them, until it has read everything seen. Under
    /// <see cref="_gate"/>.
    /// </summary>
    private Task _reading = Task.CompletedTask;

    public void Dispose() => _watchers.Dispose();

    /// <summary>
    /// Watches <paramref name="directory"/>. Failing to is a warning: a change there is not taken in.
    /// </summary>
    public void Add(string directory)
    {
        Result<IDisposable> watched = files.Watch(directory, "*.*", fullPath => Touch(directory, fullPath));
        if (!watched.Ok)
        {
            Log.Warn(
                $"The assets under {directory} are not watched, so a change there is not taken in: {watched.Error}"
            );

            return;
        }

        _watchers.Add(new Subscription(watched.Value.Dispose));
    }

    /// <summary>
    /// Once the watches have seen nothing for <see cref="_settleTime"/>, reads the asset directories again and hands them
    /// to the assets with the paths that led to it; again while they saw more meanwhile, or a directory was added as they
    /// were read. A read that fails is logged as
    /// a fault, and its paths are dropped: the next change is still taken in.
    /// </summary>
    private async Task ReadOnceSettledAsync()
    {
        while (true)
        {
            ImmutableHashSet<string> touched;
            TimeSpan left;
            lock (_gate)
            {
                left = _settleTime - Stopwatch.GetElapsedTime(_touchedTimestamp);
                touched = _touched;
                if (left <= TimeSpan.Zero)
                    _touched = touched.Clear();
            }

            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left).ConfigureAwait(false);
                continue;
            }

            ImmutableArray<string> directories = assets.Database.Directories;
            bool isTaken = assets.Reindex(directories, AssetIndex.Create(files, directories, assets.BuiltIn), touched);
            lock (_gate)
            {
                // Read while a directory was added, they are read again with it.
                if (!isTaken)
                    _touched = _touched.Union(touched);

                if (!_touched.IsEmpty)
                    continue;

                // Done, as the next touch sees, so it starts a worker of its own.
                _reading = Task.CompletedTask;

                return;
            }
        }
    }

    /// <summary>
    /// Keeps what a watch saw at <paramref name="fullPath"/> under <paramref name="directory"/>, as a path from the asset
    /// directory, a sidecar's change being its file's, and has a worker read the directories once that settles.
    /// </summary>
    private void Touch(string directory, string fullPath)
    {
        if (fullPath.Length <= directory.Length)
            return;

        string path = fullPath[(directory.Length + 1)..].Replace('\\', '/');
        if (path.EndsWith(AssetIndex.Sidecar, StringComparison.OrdinalIgnoreCase))
            path = path[..^AssetIndex.Sidecar.Length];

        lock (_gate)
        {
            _touched = _touched.Add(path);
            _touchedTimestamp = Stopwatch.GetTimestamp();
            if (!_reading.IsCompleted)
                return;

            _reading = Task.Run(ReadOnceSettledAsync);
            _reading.FireAndForget();
        }
    }
}
