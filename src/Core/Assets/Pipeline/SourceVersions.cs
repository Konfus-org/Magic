using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Magic.Files;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// What a file's version was when it last had its size and last write time, so a file that did not change is not read
/// and hashed again to know its version.
/// </summary>
/// <remarks>
/// <para>
/// A version is <see cref="AssetIndex.ComputeVersion"/> of the file, its sidecar's settings and its importer's
/// version. A record holds it with the file's size and last write time, and a hash of the settings and the importer's
/// version. A lookup is a hit only when all three match, so a change to the file, to its <c>.meta</c> or to the
/// importer makes it a miss. A file that changes with the same size and the same write time is not seen.
/// </para>
/// <para>
/// The records are kept in the user's cache folder for the project as one record, read the first time a lookup runs
/// and written a short time after the last change, off the caller's thread. A lost or unreadable record only costs a
/// read and a hash per file. Any thread.
/// </para>
/// </remarks>
internal sealed class SourceVersions : IDisposable
{
    private const string RecordName = "SourceVersions";

    /// <summary>
    /// The first int of the record, which changes with its layout.
    /// </summary>
    private const int Format = 1;

    private static readonly TimeSpan _saveDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Saves <see cref="_saveDelay"/> after the last <see cref="Remember"/>; each one starts it again, as a debounce.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Dispose disposes it through Dispose(WaitHandle), which waits out a save still running."
    )]
    private readonly Timer _save;

    private readonly IFileSystem _files;
    private readonly Caches _caches;
    private readonly Lazy<ConcurrentDictionary<string, Entry>> _entries;

    // The save that the timer started last: only the timer's callback sets it, and Dispose reads it after the timer
    // stopped.
    private Task _saving = Task.CompletedTask;

    public SourceVersions(IFileSystem files, Caches caches)
    {
        _files = files;
        _caches = caches;
        _entries = new(() => Load(caches));
        _save = new Timer(_ => _saving = SaveAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Writes the records that are not yet, and waits for it, for a short time.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The host ends the run on its own thread, with no context to deadlock, and waits at most 5 s."
    )]
    public void Dispose()
    {
        using (ManualResetEvent stopped = new(false))
        {
            // Dispose(WaitHandle) returns false, and never signals, on a timer that is already disposed.
            if (_save.Dispose(stopped))
                _ = stopped.WaitOne();
        }

        if (_entries.IsValueCreated)
            _ = Task.WhenAll(_saving, SaveAsync()).Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// What is known of <paramref name="file"/> now: its version when a record matches, and its size and write time to
    /// keep a version under.
    /// </summary>
    public Lookup Find(string file, JsonElement settings, int importerVersion)
    {
        ulong key = GetKey(settings, importerVersion);
        Result<(long Length, long Ticks)> stamp = _files.GetStamp(file);
        if (!stamp.Ok)
            return new Lookup(file, -1, 0, key, false, 0);

        (long length, long ticks) = stamp.Value;
        bool hit =
            _entries.Value.TryGetValue(file, out Entry entry)
            && entry.Length == length
            && entry.Ticks == ticks
            && entry.Key == key;

        return new Lookup(file, length, ticks, key, hit, hit ? entry.Version : 0);
    }

    /// <summary>
    /// Keeps <paramref name="version"/> as the version of the file <paramref name="lookup"/> was for.
    /// </summary>
    public void Remember(Lookup lookup, ulong version)
    {
        if (lookup.Length < 0)
            return;

        _entries.Value[lookup.File] = new Entry(lookup.Length, lookup.Ticks, lookup.Key, version);
        _ = _save.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }

    private static ulong GetKey(JsonElement settings, int importerVersion)
    {
        XxHash3 hash = new();
        hash.Append(Encoding.UTF8.GetBytes(settings.GetRawText()));
        hash.Append(BitConverter.GetBytes(importerVersion));

        return hash.GetCurrentHashAsUInt64();
    }

    private static ConcurrentDictionary<string, Entry> Load(Caches caches)
    {
        ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
        if (caches.ReadRecord(RecordName) is not { } bytes)
            return entries;

        try
        {
            using BinaryReader reader = new(new MemoryStream(bytes), Encoding.UTF8);
            if (reader.ReadInt32() != Format)
                return entries;

            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                entries[path] = new Entry(
                    reader.ReadInt64(),
                    reader.ReadInt64(),
                    reader.ReadUInt64(),
                    reader.ReadUInt64()
                );
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException)
        {
            // A record that is cut short or not ours is read as none: each version is found again.
            entries.Clear();
        }

        return entries;
    }

    [SuppressMessage(
        "Style",
        "IDE0305:Simplify collection initialization",
        Justification = "The dictionary's own ToArray is the only consistent copy: a collection expression or LINQ reads Count first and copies after, and throws when a load adds an entry between the two."
    )]
    private Task SaveAsync()
    {
        // The dictionary's own ToArray locks it for a consistent copy. A collection expression or LINQ reads Count
        // first and copies after, and throws when a load adds an entry between the two.
        KeyValuePair<string, Entry>[] snapshot = _entries.Value.ToArray();
        // The dictionary's order changes with the threads that added to it: a sorted record is the same bytes for the same
        // entries, which lets the comparison below see that nothing changed.
        Array.Sort(snapshot, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Format);
            writer.Write(snapshot.Length);
            foreach ((string path, Entry entry) in snapshot)
            {
                writer.Write(path);
                writer.Write(entry.Length);
                writer.Write(entry.Ticks);
                writer.Write(entry.Key);
                writer.Write(entry.Version);
            }
        }

        // A record that is the same as the one on disk is not written again, so a run that changed nothing leaves it.
        byte[] bytes = stream.ToArray();
        return _caches.ReadRecord(RecordName) is { } saved && saved.AsSpan().SequenceEqual(bytes)
            ? Task.CompletedTask
            : _caches.WriteRecordAsync(RecordName, bytes);
    }

    /// <summary>
    /// What <see cref="Find"/> knew of a file: <paramref name="Length"/> is -1 when the file could not be read about.
    /// </summary>
    public readonly record struct Lookup(string File, long Length, long Ticks, ulong Key, bool Hit, ulong Version);

    private readonly record struct Entry(long Length, long Ticks, ulong Key, ulong Version);
}
