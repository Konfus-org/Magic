using System.Diagnostics;

namespace Magic.Utils;

/// <summary>
/// Paths a file watcher reported, held until they have been quiet for <see cref="Settle"/>: a build or a save
/// touches a file several times, and only the last touch matters. <see cref="Add"/> from any thread (the watcher's),
/// <see cref="TakeSettled"/> on the main thread.
/// </summary>
public sealed class ChangeQueue
{
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string path)
    {
        lock (_lock)
            _pending[path] = Stopwatch.GetTimestamp();
    }

    /// <summary>Removes and returns every path quiet for <see cref="Settle"/>, in no particular order.</summary>
    public string[] TakeSettled()
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
                return [];

            string[] settled = [.. _pending.Where(p => Stopwatch.GetElapsedTime(p.Value) >= Settle).Select(p => p.Key)];
            foreach (string path in settled)
                _pending.Remove(path);

            return settled;
        }
    }
}
