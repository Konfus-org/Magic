using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Magic.Debugging;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// Keeps what is slow to make, such as an asset's import or a compiled shader, so a later run reads it and does not
/// make it again.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is a type of thing under a key: the id of what it was made from, an asset's id or a hash for what is not
/// an asset, such as a shader's source. An entry was made from one version of its source: a hash of the asset's file,
/// of the settings it was made with, and of anything else that would change it. Read with another version, it is dirty:
/// the read fails, and the caller makes it again and writes it over the entry. Any thread.
/// </para>
/// <para>
/// Entries are files in one folder per machine, <c>Magic/Cache/&lt;project&gt;/&lt;Type&gt;/&lt;key&gt;</c> under the
/// user's local application data. Every checkout and copy of Magic that runs the project shares it, so each reads what
/// another made. Each project has its own folder, because the assets of two projects can share an id. An entry holds
/// the version it was made from and the <see cref="ICache{T}.Version"/> of its type, then the bytes that its
/// <see cref="ICache{T}"/> wrote. Nothing is kept in memory: the reader of an entry keeps what it needs.
/// </para>
/// <para>
/// A project can bring its entries with it: <c>Cache/&lt;Type&gt;/&lt;key&gt;</c> in the project's folder. They are
/// read before the machine's and never written, because the folder of an installed game can be read-only. An entry
/// there that was made from another version is dirty too, and the machine's entry is read, or made, in its place.
/// </para>
/// </remarks>
public sealed class Caches
{
    /// <summary>
    /// The bytes in front of what a type's cache wrote: the version it was made from, and the type's cache's version.
    /// </summary>
    private const int HeaderSize = sizeof(ulong) + sizeof(int);

    private readonly IFileSystem _files;

    private readonly App _app;
    private Project? _project;

    internal Caches(IFileSystem files, App app)
    {
        _files = files;
        _app = app;
    }

    /// <summary>
    /// This user's cache folder for the project, a folder per type under it: read after the project's, and the one
    /// written.
    /// </summary>
    private string UserCacheFolder =>
        _files.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Magic",
            "Cache",
            Project.Name
        );

    /// <summary>
    /// The project this run's caches are for, as it was when first asked: another opened as the run ends is the next
    /// run's.
    /// </summary>
    private Project Project =>
        _project ??= _app.Project ?? throw new InvalidOperationException("Nothing is cached until a project is open.");

    /// <summary>
    /// What is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made from <paramref name="version"/>:
    /// from the project, else from the machine.
    /// </summary>
    /// <remarks>
    /// It fails when neither holds it, or when what they hold was made from another version or by another
    /// <see cref="ICache{T}.Version"/>.
    /// </remarks>
    public async Task<Result<T>> ReadAsync<T>(ulong key, ulong version, ICache<T> cache, CancellationToken cancel)
    {
        // The cache folder the project brings with it is read first, and never written.
        string projectCache = _files.Combine(_files.GetParent(Project.Path), "Cache");
        Result<T> fromProject = await ReadEntryAsync(GetEntryFile<T>(projectCache, key), version, cache, cancel)
            .ConfigureAwait(false);

        return fromProject.Ok
            ? fromProject
            : await ReadEntryAsync(GetEntryFile<T>(UserCacheFolder, key), version, cache, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps <paramref name="value"/> as what is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made
    /// from <paramref name="version"/>, in place of what was there.
    /// </summary>
    /// <remarks>
    /// A reader, or the next run after a crash, never finds half an entry. When it cannot keep the entry, it logs a
    /// warning, and the entry is made again next time.
    /// </remarks>
    public async Task WriteAsync<T>(ulong key, ulong version, T value, ICache<T> cache, CancellationToken cancel)
    {
        // The versions, then what the type's cache writes.
        ArrayBufferWriter<byte> entry = new();
        Span<byte> header = entry.GetSpan(HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header, version);
        BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(ulong)..], cache.Version);
        entry.Advance(HeaderSize);
        cache.Write(value, entry);

        string file = GetEntryFile<T>(UserCacheFolder, key);
        Result written = await _files.WriteAsync(file, entry.WrittenMemory, cancel).ConfigureAwait(false);
        if (!written.Ok)
            Log.Warn($"{file} could not be written, so it is made again next time: {written.Error}");
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
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Bytes a type's cache cannot read, whatever it throws, are dirty: they are made again."
    )]
    private async Task<Result<T>> ReadEntryAsync<T>(
        string file,
        ulong version,
        ICache<T> cache,
        CancellationToken cancel
    )
    {
        if (!_files.FileExists(file))
            return Result.Failure($"nothing is cached at {file}.");

        Result<byte[]> read = await _files.ReadBytesAsync(file, cancel).ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure($"{file} could not be read: {read.Error}");

        // The header: the version it was made from, and its type's cache's. A dirty entry is left for the write that
        // follows to replace: deleting it here would collide with another reader of the same entry, and it is never
        // read as valid anyway.
        byte[] entry = read.Value;
        if (
            entry.Length < HeaderSize
            || BinaryPrimitives.ReadUInt64LittleEndian(entry) != version
            || BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(sizeof(ulong))) != cache.Version
        )
        {
            return Result.Failure($"{file} is dirty: it was made from something else.");
        }

        // Then the bytes the type's cache wrote.
        try
        {
            return cache.Read(entry.AsSpan(HeaderSize));
        }
        catch (Exception ex)
        {
            return Result.Failure($"{file} is dirty: its bytes are not a {typeof(T).Name}: {ex.Message}");
        }
    }
}
