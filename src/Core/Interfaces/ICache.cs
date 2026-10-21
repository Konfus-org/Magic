using System.IO;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// How one type of cached thing is written and read back: provided by the gem that makes it, as it provides any
/// interface. <see cref="Caches"/> asks for the one of a type (through <see cref="IServiceProvider"/>) each time it
/// caches or reads one, and keeps its bytes as they are. Safe from any thread, for several at once.
/// </summary>
public interface ICache<T>
{
    /// <summary>
    /// Raised whenever what it writes, or how what it caches is made, changes, so that what an older one wrote is
    /// dirty.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Writes <paramref name="value"/> into <paramref name="bytes"/>, from where it stands.
    /// </summary>
    void Write(T value, Stream bytes);

    /// <summary>
    /// What <see cref="Write"/> wrote into <paramref name="bytes"/>, or why they are not that. Bytes cut short fail the
    /// read whether it returns why or throws.
    /// </summary>
    Result<T> Read(Stream bytes);
}
