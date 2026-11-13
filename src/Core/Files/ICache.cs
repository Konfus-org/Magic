using System.Buffers;
using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// Writes one type of cached thing and reads it back; the gem that makes the thing gives its own to
/// <see cref="Caches"/>.
/// </summary>
/// <remarks>
/// When an asset importer also implements it, Core caches what it imports by the asset's id and version. Only an
/// importer whose asset loads no other assets may do this: on a hit the importer does not run, so nothing that it would
/// load is recorded as a dependency. Safe from any thread, for several calls at the same time.
/// </remarks>
public interface ICache<T>
{
    /// <summary>
    /// Raised whenever what it writes, or how what it caches is made, changes, so that what an older one wrote is
    /// dirty.
    /// </summary>
    int Version { get; }

    void Write(T value, IBufferWriter<byte> bytes);

    /// <summary>
    /// What <see cref="Write"/> wrote into <paramref name="bytes"/>, or why they are not that.
    /// </summary>
    /// <remarks>
    /// When the bytes are cut short, the read fails, whether it returns why or throws.
    /// </remarks>
    Result<T> Read(ReadOnlySpan<byte> bytes);
}
