using System.Buffers;
using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// How one type of cached thing is written and read back: the gem that makes it passes its own to
/// <see cref="Caches"/>. An asset importer that also implements it has what it imports cached by the asset's id and
/// version, so only one whose asset loads no other assets may: on a hit the importer doesn't run, so nothing it would
/// load is recorded as a dependency. Safe from any thread, for several at once.
/// </summary>
public interface ICache<T>
{
    /// <summary>
    /// Raised whenever what it writes, or how what it caches is made, changes, so that what an older one wrote is
    /// dirty.
    /// </summary>
    int Version { get; }

    void Write(T value, IBufferWriter<byte> bytes);

    /// <summary>
    /// What <see cref="Write"/> wrote into <paramref name="bytes"/>, or why they are not that. Bytes cut short fail the
    /// read whether it returns why or throws.
    /// </summary>
    Result<T> Read(ReadOnlySpan<byte> bytes);
}
