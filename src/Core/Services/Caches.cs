using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// What is slow to make, kept so that a later run reads it rather than making it again: a model's clusters, a compiled
/// shader. Each entry is a type of thing under a key, the id of what it was made from: an asset's, so any asset can
/// have one of each type, or a hash naming what is not an asset, such as a shader's source and stage. An entry was made
/// from one version of its source (a hash of the asset's file, of the settings it was made with, of whatever else would
/// make it come out otherwise); read with another, it is dirty: the read fails, and the caller makes it again and
/// writes it over it. Any thread.
/// <para>
/// Entries are files in one folder per machine, shared by every checkout and copy of Magic that runs the project, so
/// what one made the others read: <c>Magic/Cache/</c> under the user's local application data, or the folder the
/// <c>MagicCache</c> environment variable names, then <c>&lt;project&gt;/&lt;Type&gt;/&lt;key&gt;</c>. A project has
/// its own, as two projects' assets can share an id. An entry holds the version it was made from and its type's
/// <see cref="ICache{T}.Version"/>, then the bytes the type's <see cref="ICache{T}"/> wrote, which a gem provides and
/// which is asked for through <see cref="IServiceProvider"/> on every read and write. As an entry is read only at the
/// version it was made from, a run that changed what it is made from makes its own over it, and a reader in another
/// process never finds half of one. Nothing is kept in memory: whoever reads an entry keeps what it needs of it.
/// </para>
/// <para>
/// A project can bring its entries with it, as a Release build bakes them: <c>Cache/&lt;Type&gt;/&lt;key&gt;</c> in
/// the project's own folder, read before the machine's and never written, as an installed game's folder may not be
/// writable. One made from another version, as a preset with other settings makes, is dirty there too, and the
/// machine's is read, or made, in its place.
/// </para>
/// </summary>
public sealed class Caches
{
    /// <summary>
    /// The bytes in front of what a type's cache wrote: the version it was made from, and the type's cache's version.
    /// </summary>
    private const int HeaderSize = sizeof(ulong) + sizeof(int);

    private readonly IFileSystem _files;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Where the project's entries are on this machine, a folder per type under it.
    /// </summary>
    private readonly string _folder;

    /// <summary>
    /// Where the entries the project brings with it are, a folder per type under it, read first and never written.
    /// </summary>
    private readonly string _bundled;

    internal Caches(IFileSystem files, IServiceProvider services, Project project)
    {
        _files = files;
        _services = services;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string machine = Environment.GetEnvironmentVariable("MagicCache") is { Length: > 0 } named
            ? named
            : files.Combine(local, "Magic", "Cache");
        _folder = files.Combine(machine, project.Name);
        _bundled = files.Combine(project.Root, "Cache");
    }

    /// <summary>
    /// What is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made from <paramref name="version"/>:
    /// what the project brings, else what the machine keeps; why not when neither holds it, or when what they hold was
    /// made from another version, or by another <see cref="ICache{T}.Version"/>.
    /// </summary>
    public async Task<Result<T>> ReadAsync<T>(ulong key, ulong version, CancellationToken cancel)
    {
        if (_services.GetProvided<ICache<T>>(out _) is not { } cache)
        {
            return Result.Failure<T>($"no gem the project lists caches {typeof(T).Name}s.");
        }

        Result<T> bundled = await ReadAsync(cache, GetFile<T>(_bundled, key), version, cancel).ConfigureAwait(false);
        return bundled.Ok
            ? bundled
            : await ReadAsync(cache, GetFile<T>(_folder, key), version, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps <paramref name="value"/> as what is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made
    /// from <paramref name="version"/>, in place of what was. It is written beside the entry, then moved over it, so a
    /// reader, or the next run after a crash, never finds half an entry. One that cannot be kept is a warning: it is
    /// made again next time.
    /// </summary>
    public async Task WriteAsync<T>(ulong key, ulong version, T value, CancellationToken cancel)
    {
        if (_services.GetProvided<ICache<T>>(out _) is not { } cache)
        {
            Debugging.Warn($"No gem the project lists caches {typeof(T).Name}s, so one is not kept.");
            return;
        }

        string file = GetFile<T>(_folder, key);
        Result written = await _files.WriteAsync(file, ToBytes(cache, version, value), cancel).ConfigureAwait(false);
        if (!written.Ok) Debugging.Warn($"{file} could not be written, so it is made again next time: {written.Error}");
    }

    /// <summary>
    /// <paramref name="value"/> as an entry: the versions, then what <paramref name="cache"/> writes of it.
    /// </summary>
    private static ReadOnlyMemory<byte> ToBytes<T>(ICache<T> cache, ulong version, T value)
    {
        using MemoryStream stream = new();
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header, version);
        BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(ulong)..], cache.Version);
        stream.Write(header);
        cache.Write(value, stream);
        return stream.GetBuffer().AsMemory(0, (int)stream.Length);
    }

    /// <summary>
    /// What the entry <paramref name="bytes"/> holds, read by <paramref name="cache"/>, when it was made from
    /// <paramref name="version"/> by the cache's version; why not otherwise.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Bytes a type's cache cannot read, whatever it throws, are dirty: "
            + "they are deleted and made again.")]
    private static Result<T> Read<T>(ICache<T> cache, byte[] bytes, ulong version)
    {
        if (bytes.Length < HeaderSize
            || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != version
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(sizeof(ulong))) != cache.Version)
        {
            return Result.Failure<T>("it was made from something else.");
        }

        try
        {
            using MemoryStream stream = new(bytes, HeaderSize, bytes.Length - HeaderSize, writable: false);
            return cache.Read(stream);
        }
        catch (Exception ex)
        {
            return Result.Failure<T>($"its bytes are not a {typeof(T).Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Where what is cached of <paramref name="key"/> as a <typeparamref name="T"/> is under <paramref name="folder"/>.
    /// </summary>
    private string GetFile<T>(string folder, ulong key)
    {
        return _files.Combine(folder, typeof(T).Name, key.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// What the entry at <paramref name="file"/> holds, read by <paramref name="cache"/>, made from
    /// <paramref name="version"/>; why not when there is none, or it was made from something else.
    /// </summary>
    private async Task<Result<T>> ReadAsync<T>(ICache<T> cache, string file, ulong version, CancellationToken cancel)
    {
        if (!_files.FileExists(file)) return Result.Failure<T>($"nothing is cached at {file}.");

        Result<byte[]> bytes = await _files.ReadAsync(file, cancel).ConfigureAwait(false);
        if (!bytes.Ok) return Result.Failure<T>($"{file} could not be read: {bytes.Error}");

        // A dirty entry is left for the write that follows to replace: deleting it here would collide with another
        // reader of the same entry, and it is never read as valid anyway.
        Result<T> read = Read(cache, bytes.Value, version);
        return read.Ok ? read : Result.Failure<T>($"{file} is dirty: {read.Error}");
    }
}
