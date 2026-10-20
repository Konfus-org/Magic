using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// What is slow to make, kept so that a later run reads it rather than making it again: a model's clusters, a
/// compiled shader. Each entry is a type of thing under a key, the id of what it was made from: an asset's, so any asset
/// can have one of each type, or a hash naming what is not an asset, such as a shader's source and stage. An entry was
/// made from one version of its source (a hash of the asset's file, of the settings it was made with, of whatever else
/// would make it come out otherwise); read with another, it is dirty: the read fails, and the caller makes it again and
/// writes it over it. Any thread.
/// <para>
/// Entries are files beside Magic, in <c>Cache/</c>, as the log's are in <c>Logs/</c>: <c>Cache/&lt;Type&gt;/&lt;key&gt;</c>,
/// the version it was made from and its type's <see cref="ICache{T}.Version"/>, then the bytes the type's
/// <see cref="ICache{T}"/> wrote, which a gem provides and which is asked for through <see cref="IServiceProvider"/> on
/// every read and write. Nothing is kept in memory: whoever reads an entry keeps what it needs of it.
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

    internal Caches(IFileSystem files, IServiceProvider services)
    {
        _files = files;
        _services = services;
    }

    /// <summary>
    /// What is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made from <paramref name="version"/>; why
    /// not when nothing is, or when what is was made from another version, or by another <see cref="ICache{T}.Version"/>.
    /// </summary>
    public async Task<Result<T>> ReadAsync<T>(ulong key, ulong version, CancellationToken cancel)
    {
        if (_services.Provided<ICache<T>>(out _) is not { } cache)
        {
            return Result.Failure<T>($"no gem the project lists caches {typeof(T).Name}s.");
        }

        string file = FileOf<T>(key);
        if (!_files.FileExists(file))
        {
            return Result.Failure<T>($"nothing is cached at {file}.");
        }

        Result<byte[]> bytes = await _files.ReadAsync(file, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
        {
            return Result.Failure<T>($"{file} could not be read: {bytes.Error}");
        }

        // A dirty entry is left for the write that follows to replace: deleting it here would collide with another
        // reader of the same entry, and it is never read as valid anyway.
        Result<T> read = Read(cache, bytes.Value, version);
        return read.Ok ? read : Result.Failure<T>($"{file} is dirty: {read.Error}");
    }

    /// <summary>
    /// Keeps <paramref name="value"/> as what is cached of <paramref name="key"/> as a <typeparamref name="T"/>, made from
    /// <paramref name="version"/>, in place of what was. It is written beside the entry, then moved over it, so a reader,
    /// or the next run after a crash, never finds half an entry. One that cannot be kept is a warning: it is made again
    /// next time.
    /// </summary>
    public async Task WriteAsync<T>(ulong key, ulong version, T value, CancellationToken cancel)
    {
        if (_services.Provided<ICache<T>>(out _) is not { } cache)
        {
            Debugging.Warn($"No gem the project lists caches {typeof(T).Name}s, so one is not kept.");
            return;
        }

        string file = FileOf<T>(key);
        Result written = await _files.WriteAsync(file, Bytes(cache, version, value), cancel).ConfigureAwait(false);
        if (!written.Ok)
        {
            Debugging.Warn($"{file} could not be written, so it is made again next time: {written.Error}");
        }
    }

    /// <summary>
    /// Where what is cached of <paramref name="key"/> as a <typeparamref name="T"/> is: beside Magic, in <c>Cache/</c>.
    /// </summary>
    private string FileOf<T>(ulong key)
    {
        return _files.Combine(AppContext.BaseDirectory, "Cache", typeof(T).Name, key.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <paramref name="value"/> as an entry: the versions, then what <paramref name="cache"/> writes of it.
    /// </summary>
    private static ReadOnlyMemory<byte> Bytes<T>(ICache<T> cache, ulong version, T value)
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
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Bytes a type's cache cannot read, whatever it throws, are dirty: they are deleted and made again.")]
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
}
