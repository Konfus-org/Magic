using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Caching;

/// <summary>
/// What is slow to make, kept so that a later run reads it rather than making it again: an asset's import, a compiled
/// shader. Each entry is a type of thing under a key, the id of what it was made from: an asset's, or a hash naming
/// what is not an asset, such as a shader's source. An entry was made from one version of its source (a hash of the
/// asset's file, of the settings it was made with, of whatever else would make it come out otherwise); read with
/// another, it is dirty: the read fails, and the caller makes it again and writes it over it. Any thread.
/// <para>
/// Entries are files in one folder per machine, shared by every checkout and copy of Magic that runs the project, so
/// what one made the others read: <c>&lt;project&gt;/&lt;Type&gt;/&lt;key&gt;</c> under <see cref="GetRoot"/>. A project
/// has its own, as two projects' assets can share an id. An entry holds the version it was made from and its type's
/// <see cref="ICache{T}.Version"/>, then the bytes its <see cref="ICache{T}"/> wrote. Nothing is kept in memory:
/// whoever reads an entry keeps what it needs of it.
/// </para>
/// <para>
/// A project can bring its entries with it: <c>Cache/&lt;Type&gt;/&lt;key&gt;</c> in the project's own folder, read
/// before the machine's and never written, as an installed game's folder may not be writable. One made from another
/// version is dirty there too, and the machine's is read, or made, in its place.
/// </para>
/// </summary>
public sealed class Caches
{
    /// <summary>
    /// The bytes in front of what a type's cache wrote: the version it was made from, and the type's cache's version.
    /// </summary>
    private const int HeaderSize = sizeof(ulong) + sizeof(int);

    private readonly IFileSystem _files;

    /// <summary>
    /// This user's cache folder for the project, a folder per type under it: read after the project's, and the one
    /// written.
    /// </summary>
    private readonly string _userCacheFolder;

    /// <summary>
    /// The cache folder the project brings with it, a folder per type under it: read first and never written.
    /// </summary>
    private readonly string _projectCacheFolder;

    internal Caches(IFileSystem files, string projectName, string projectFolder)
    {
        _files = files;
        _userCacheFolder = files.Combine(GetRoot(files), projectName);
        _projectCacheFolder = files.Combine(projectFolder, "Cache");
    }

    /// <summary>
    /// What is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made from <paramref name="version"/>:
    /// what the project brings, else what the machine keeps; why not when neither holds it, or when what they hold was
    /// made from another version, or by another <see cref="ICache{T}.Version"/>.
    /// </summary>
    public async Task<Result<T>> ReadAsync<T>(ulong key, ulong version, ICache<T> cache, CancellationToken cancel)
    {
        Result<T> fromProject = await ReadEntryAsync(GetEntryFile<T>(_projectCacheFolder, key), version, cache, cancel)
            .ConfigureAwait(false);

        return fromProject.Ok
            ? fromProject
            : await ReadEntryAsync(GetEntryFile<T>(_userCacheFolder, key), version, cache, cancel)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps <paramref name="value"/> as what is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made
    /// from <paramref name="version"/>, in place of what was. A reader, or the next run after a crash, never finds half
    /// an entry. One that cannot be kept is a warning: it is made again next time.
    /// </summary>
    public async Task WriteAsync<T>(ulong key, ulong version, T value, ICache<T> cache, CancellationToken cancel)
    {
        string file = GetEntryFile<T>(_userCacheFolder, key);
        Result written = await _files.WriteAsync(file, ToEntry(version, value, cache), cancel).ConfigureAwait(false);
        if (!written.Ok)
            Log.Warn($"{file} could not be written, so it is made again next time: {written.Error}");
    }

    /// <summary>
    /// The folder every project's cache is under on this machine: <c>Magic/Cache/</c> under the user's local
    /// application data.
    /// </summary>
    private static string GetRoot(IFileSystem files) =>
        files.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magic", "Cache");

    /// <summary>
    /// <paramref name="value"/> as an entry: the versions, then what <paramref name="cache"/> writes of it.
    /// </summary>
    private static ReadOnlyMemory<byte> ToEntry<T>(ulong version, T value, ICache<T> cache)
    {
        ArrayBufferWriter<byte> entry = new();
        Span<byte> header = entry.GetSpan(HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header, version);
        BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(ulong)..], cache.Version);
        entry.Advance(HeaderSize);
        cache.Write(value, entry);

        return entry.WrittenMemory;
    }

    /// <summary>
    /// What <paramref name="entry"/> holds, read by <paramref name="cache"/>, when it was made from
    /// <paramref name="version"/> by the cache's version; why not otherwise.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Bytes a type's cache cannot read, whatever it throws, are dirty: they are made again."
    )]
    private static Result<T> FromEntry<T>(ReadOnlySpan<byte> entry, ulong version, ICache<T> cache)
    {
        if (
            entry.Length < HeaderSize
            || BinaryPrimitives.ReadUInt64LittleEndian(entry) != version
            || BinaryPrimitives.ReadInt32LittleEndian(entry[sizeof(ulong)..]) != cache.Version
        )
        {
            return Result.Failure("it was made from something else.");
        }

        try
        {
            return cache.Read(entry[HeaderSize..]);
        }
        catch (Exception ex)
        {
            return Result.Failure($"its bytes are not a {typeof(T).Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Where what is cached of <paramref name="key"/> as a <typeparamref name="T"/> is under <paramref name="folder"/>.
    /// </summary>
    private string GetEntryFile<T>(string folder, ulong key) =>
        _files.Combine(folder, typeof(T).Name, key.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// What the entry at <paramref name="file"/> holds, read by <paramref name="cache"/>, made from
    /// <paramref name="version"/>; why not when there is none, or it was made from something else.
    /// </summary>
    private async Task<Result<T>> ReadEntryAsync<T>(
        string file,
        ulong version,
        ICache<T> cache,
        CancellationToken cancel
    )
    {
        if (!_files.FileExists(file))
            return Result.Failure($"nothing is cached at {file}.");

        Result<byte[]> bytes = await _files.ReadBytesAsync(file, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure($"{file} could not be read: {bytes.Error}");

        // A dirty entry is left for the write that follows to replace: deleting it here would collide with another
        // reader of the same entry, and it is never read as valid anyway.
        Result<T> read = FromEntry(bytes.Value, version, cache);

        return read.Ok ? read : Result.Failure($"{file} is dirty: {read.Error}");
    }
}
