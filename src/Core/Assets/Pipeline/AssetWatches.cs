using System.Collections.Immutable;
using System.Diagnostics;
using Magic.Debugging;
using Magic.Files;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Watches the asset directories, and reads them again for <paramref name="assets"/> once the changes settle.
/// </summary>
internal sealed class AssetWatches(IFileSystem files, AssetManager assets) : IDisposable
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
    /// Reads the asset directories again once the watches see no change for <see cref="_settleTime"/>, and gives them
    /// to the assets with the changed paths.
    /// </summary>
    /// <remarks>
    /// It reads again while the watches see more changes, or when a directory was added during the read. A failed read
    /// is logged as a fault and its paths are dropped; the next change is still taken in.
    /// </remarks>
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
                // A directory was added during the read, so read again with it.
                if (!isTaken)
                    _touched = _touched.Union(touched);

                if (!_touched.IsEmpty)
                    continue;

                // Done: the next touch sees this and starts a new worker.
                _reading = Task.CompletedTask;

                return;
            }
        }
    }

    /// <summary>
    /// Records the change that a watch saw at <paramref name="fullPath"/>, and starts a worker that reads the
    /// directories once the changes settle.
    /// </summary>
    /// <remarks>
    /// It keeps the path relative to <paramref name="directory"/>. A change to a sidecar counts as a change to its
    /// file.
    /// </remarks>
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
